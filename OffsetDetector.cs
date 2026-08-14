using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace Agent64AimMods
{
    /// <summary>
    /// Finds the aim target field by behaviour rather than by address, so a game update
    /// that moves it can be recovered from without anyone reverse engineering it again.
    /// </summary>
    /// <remarks>
    /// The reticle eases toward the target, which means every frame satisfies
    /// <c>delta == k * error</c>, where <c>delta</c> is how far the reticle moved and
    /// <c>error</c> is how far it was from the target beforehand. Fitting that line for
    /// every Vector2 field on the class and keeping the best fit identifies the target:
    /// unrelated fields produce no correlation, and a field the reticle merely resembles
    /// produces a poor one. The fit is scale free, so it still works if a patch changes
    /// the easing constant.
    /// </remarks>
    internal sealed class OffsetDetector
    {
        /// <summary>Frames of actual reticle movement before a result is considered at all.</summary>
        private const int MinimumSamples = 120;

        /// <summary>
        /// Distance the reticle has to have travelled, in pixels, before a fit is trusted.
        /// Movement is the only thing carrying information here: a still reticle satisfies
        /// <c>delta = k * error</c> for nothing in particular.
        /// </summary>
        private const double MinimumSignal = 1500.0;

        /// <summary>Movement below this in a frame is treated as the reticle sitting still.</summary>
        private const float MovementThreshold = 0.5f;

        /// <summary>Share of the reticle's movement the winner has to explain.</summary>
        private const double MinimumFitQuality = 0.9;

        /// <summary>Plausible bounds for the easing constant, per frame.</summary>
        private const double MinimumRate = 0.01;
        private const double MaximumRate = 0.95;

        /// <summary>How much better than the runner up the winner has to be.</summary>
        private const double RequiredMargin = 0.15;

        /// <summary>Frames after which a fruitless attempt restarts, roughly 30 seconds.</summary>
        private const int RetryAfterFrames = 5400;

        private const string TargetTypeName = "UnityEngine.Vector2";
        private const int FieldAttributeStatic = 0x10;

        /// <summary>Instance fields start after the IL2CPP object header.</summary>
        private const int ObjectHeaderBytes = 0x10;

        private readonly List<Candidate> candidates = new();
        private readonly IntPtr instance;

        private Vector2 previousPosition;
        private bool hasPrevious;
        private int frames;
        private int samples;
        private double signal;

        internal OffsetDetector(IntPtr instance)
        {
            this.instance = instance;

            foreach ((IntPtr field, int offset) in Vector2FieldsOf(instance))
            {
                candidates.Add(new Candidate(field, offset));
            }
        }

        /// <summary>Number of fields being considered.</summary>
        internal int CandidateCount => candidates.Count;


        /// <summary>
        /// Feeds one frame of evidence in. Call after the game has eased the reticle, and
        /// do not write to the reticle while detecting or the signal is destroyed.
        /// </summary>
        /// <param name="position">The reticle's position this frame.</param>
        /// <param name="offset">The detected offset, once one is certain.</param>
        /// <returns><c>true</c> when detection has succeeded.</returns>
        internal bool Observe(Vector2 position, out int offset)
        {
            offset = -1;

            if (!hasPrevious)
            {
                previousPosition = position;
                hasPrevious = true;
                return false;
            }

            Vector2 delta = position - previousPosition;

            // Still frames are kept in the fit. They cost the true target nothing, since
            // both its error and the movement are zero, while a constant field far from the
            // reticle piles up error it cannot explain. They just don't count as progress.
            foreach (Candidate candidate in candidates)
            {
                Vector2 value = ReadVector2(instance, candidate.Field);
                candidate.Accumulate(value - previousPosition, delta);
            }

            previousPosition = position;
            frames++;

            float travelled = Mathf.Abs(delta.x) + Mathf.Abs(delta.y);
            if (travelled >= MovementThreshold)
            {
                samples++;
                signal += travelled;
            }

            if (frames >= RetryAfterFrames)
            {
                Reset();
                return false;
            }

            if (samples < MinimumSamples || signal < MinimumSignal)
            {
                return false;
            }

            return TrySelect(out offset);
        }

        /// <summary>Picks the best fit, but only if it is both good and clearly ahead.</summary>
        private bool TrySelect(out int offset)
        {
            offset = -1;

            Candidate best = null;
            double bestQuality = 0.0;
            double runnerUpQuality = 0.0;

            foreach (Candidate candidate in candidates)
            {
                double quality = candidate.FitQuality;
                if (quality > bestQuality)
                {
                    runnerUpQuality = bestQuality;
                    bestQuality = quality;
                    best = candidate;
                }
                else if (quality > runnerUpQuality)
                {
                    runnerUpQuality = quality;
                }
            }

            if (best == null || bestQuality < MinimumFitQuality)
            {
                return false;
            }

            if (bestQuality - runnerUpQuality < RequiredMargin)
            {
                return false;
            }

            double rate = best.Rate;
            if (rate < MinimumRate || rate > MaximumRate)
            {
                return false;
            }

            Plugin.Logger.LogInfo(
                $"Detected aim target at 0x{best.Offset:X} " +
                $"(easing {rate:0.###} per frame, fit {bestQuality:0.###}, " +
                $"next best {runnerUpQuality:0.###}).");

            offset = best.Offset;
            return true;
        }

        private void Reset()
        {
            foreach (Candidate candidate in candidates)
            {
                candidate.Reset();
            }

            frames = 0;
            samples = 0;
            signal = 0.0;
            hasPrevious = false;
        }

        /// <summary>Every non-static Vector2 field on the class and its bases.</summary>
        private static IEnumerable<(IntPtr Field, int Offset)> Vector2FieldsOf(IntPtr instance)
        {
            for (IntPtr klass = IL2CPP.il2cpp_object_get_class(instance);
                 klass != IntPtr.Zero;
                 klass = IL2CPP.il2cpp_class_get_parent(klass))
            {
                IntPtr iterator = IntPtr.Zero;
                IntPtr field;

                while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref iterator)) != IntPtr.Zero)
                {
                    if ((IL2CPP.il2cpp_field_get_flags(field) & FieldAttributeStatic) != 0)
                    {
                        continue;
                    }

                    IntPtr type = IL2CPP.il2cpp_field_get_type(field);
                    if (type == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (Marshal.PtrToStringUTF8(IL2CPP.il2cpp_type_get_name(type)) != TargetTypeName)
                    {
                        continue;
                    }

                    int offset = (int)IL2CPP.il2cpp_field_get_offset(field);
                    if (offset >= ObjectHeaderBytes)
                    {
                        yield return (field, offset);
                    }
                }
            }
        }

        private static unsafe Vector2 ReadVector2(IntPtr instance, IntPtr field)
        {
            Vector2 value = default;
            IL2CPP.il2cpp_field_get_value(instance, field, &value);
            return value;
        }

        /// <summary>One field under consideration, with the running least-squares fit.</summary>
        private sealed class Candidate
        {
            private double errorDotDelta;
            private double errorSquared;
            private double deltaSquared;

            internal Candidate(IntPtr field, int offset)
            {
                Field = field;
                Offset = offset;
            }

            internal IntPtr Field { get; }

            internal int Offset { get; }

            /// <summary>The easing constant implied by the samples so far.</summary>
            internal double Rate => errorSquared > 0.0 ? errorDotDelta / errorSquared : 0.0;

            /// <summary>
            /// Share of the reticle's movement this field explains, from 0 to 1. This is the
            /// coefficient of determination for <c>delta = Rate * error</c>.
            /// </summary>
            internal double FitQuality =>
                errorSquared > 0.0 && deltaSquared > 0.0
                    ? errorDotDelta * errorDotDelta / (errorSquared * deltaSquared)
                    : 0.0;

            /// <summary>Both axes count as independent evidence for the same fit.</summary>
            internal void Accumulate(Vector2 error, Vector2 delta)
            {
                errorDotDelta += (double)error.x * delta.x + (double)error.y * delta.y;
                errorSquared += (double)error.x * error.x + (double)error.y * error.y;
                deltaSquared += (double)delta.x * delta.x + (double)delta.y * delta.y;
            }

            internal void Reset()
            {
                errorDotDelta = 0.0;
                errorSquared = 0.0;
                deltaSquared = 0.0;
            }
        }
    }
}
