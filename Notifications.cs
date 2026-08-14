using System.Collections.Generic;
using UnityEngine;

namespace Agent64AimMods
{
    /// <summary>Where on screen notifications stack up.</summary>
    internal enum NotificationAnchor
    {
        TopLeft,
        TopCentre,
        TopRight,
        BottomLeft,
        BottomCentre,
        BottomRight
    }

    /// <summary>
    /// On screen messages, so mod state doesn't mean alt tabbing to read the console.
    /// </summary>
    /// <remarks>
    /// Two kinds. A status line stays up for as long as whatever set it is still happening,
    /// which is what open ended work like offset detection needs. Everything else is a
    /// message that fades out on its own.
    /// </remarks>
    internal sealed class Notifications
    {
        /// <summary>How long a message spends fading out at the end of its life.</summary>
        private const float FadeSeconds = 0.6f;

        /// <summary>Warnings outlive normal messages by this much, since they matter more.</summary>
        private const float WarningMultiplier = 2.5f;

        /// <summary>Floor on how long a warning stays up, in seconds.</summary>
        private const float MinimumWarningSeconds = 6f;

        /// <summary>Gap between stacked messages, in pixels.</summary>
        private const float LineSpacing = 4f;

        /// <summary>Distance from the screen edge, in pixels.</summary>
        private const float Margin = 24f;

        /// <summary>Older messages are dropped once this many are queued.</summary>
        private const int MaxVisible = 4;

        private static readonly Color InfoColour = new(1f, 1f, 1f, 1f);
        private static readonly Color WarningColour = new(1f, 0.78f, 0.25f, 1f);

        private readonly List<Message> messages = new();
        private string status;
        private GUIStyle style;
        private int styleFontSize;

        /// <summary>Queues a message that fades out on its own.</summary>
        internal void Show(string text, bool warning = false)
        {
            if (!Enabled || string.IsNullOrEmpty(text))
            {
                return;
            }

            float seconds = Plugin.Options.NotificationSeconds.Value;
            if (warning)
            {
                seconds = Mathf.Max(seconds * WarningMultiplier, MinimumWarningSeconds);
            }

            messages.Add(new Message(text, Time.unscaledTime + seconds, warning));

            if (messages.Count > MaxVisible)
            {
                messages.RemoveRange(0, messages.Count - MaxVisible);
            }
        }

        /// <summary>
        /// Sets the line that stays up until it is changed or cleared. Use for work that
        /// runs for an unknown length of time.
        /// </summary>
        internal void SetStatus(string text) => status = Enabled ? text : null;

        internal void ClearStatus() => status = null;

        /// <summary>Drops expired messages. Called once per frame, not once per GUI event.</summary>
        internal void Prune()
        {
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (Time.unscaledTime >= messages[i].Expiry)
                {
                    messages.RemoveAt(i);
                }
            }
        }

        /// <summary>Draws the status line and the queue. Must be called from OnGUI.</summary>
        internal void Draw()
        {
            if (!Enabled || (status == null && messages.Count == 0))
            {
                return;
            }

            NotificationAnchor anchor = Plugin.Options.NotificationAnchor.Value;
            GUIStyle current = StyleFor(anchor);

            float lineHeight = current.fontSize + 8f;
            bool fromTop = anchor is NotificationAnchor.TopLeft
                or NotificationAnchor.TopCentre
                or NotificationAnchor.TopRight;

            Color previous = GUI.color;
            int line = 0;

            if (status != null)
            {
                DrawLine(status, InfoColour, 1f, line++, lineHeight, fromTop, current);
            }

            foreach (Message message in messages)
            {
                float alpha = Mathf.Clamp01((message.Expiry - Time.unscaledTime) / FadeSeconds);
                Color colour = message.IsWarning ? WarningColour : InfoColour;

                DrawLine(message.Text, colour, alpha, line++, lineHeight, fromTop, current);
            }

            GUI.color = previous;
        }

        private static void DrawLine(
            string text, Color colour, float alpha, int index, float lineHeight, bool fromTop, GUIStyle style)
        {
            float offset = index * (lineHeight + LineSpacing);
            float y = fromTop
                ? Margin + offset
                : Screen.height - Margin - lineHeight - offset;

            var area = new Rect(Margin, y, Screen.width - (Margin * 2f), lineHeight);

            // Drawn twice, offset by a pixel, so the text stays readable against a bright
            // skybox as well as a dark corridor.
            GUI.color = new Color(0f, 0f, 0f, alpha * 0.8f);
            GUI.Label(new Rect(area.x + 2f, area.y + 2f, area.width, area.height), text, style);

            GUI.color = new Color(colour.r, colour.g, colour.b, alpha);
            GUI.Label(area, text, style);
        }

        private static bool Enabled => Plugin.Options.ShowNotifications.Value;

        /// <summary>
        /// Builds the label style, which can only happen inside OnGUI because it reads
        /// <see cref="GUI.skin"/>. Rebuilt if the configured size changes.
        /// </summary>
        private GUIStyle StyleFor(NotificationAnchor anchor)
        {
            int fontSize = Plugin.Options.NotificationFontSize.Value;

            if (style == null || styleFontSize != fontSize)
            {
                style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = fontSize,
                    fontStyle = FontStyle.Bold,
                    richText = false,
                    wordWrap = false
                };

                styleFontSize = fontSize;
            }

            style.alignment = anchor switch
            {
                NotificationAnchor.TopLeft => TextAnchor.UpperLeft,
                NotificationAnchor.TopCentre => TextAnchor.UpperCenter,
                NotificationAnchor.TopRight => TextAnchor.UpperRight,
                NotificationAnchor.BottomLeft => TextAnchor.LowerLeft,
                NotificationAnchor.BottomCentre => TextAnchor.LowerCenter,
                _ => TextAnchor.LowerRight
            };

            return style;
        }

        private readonly struct Message
        {
            internal Message(string text, float expiry, bool isWarning)
            {
                Text = text;
                Expiry = expiry;
                IsWarning = isWarning;
            }

            internal string Text { get; }

            internal float Expiry { get; }

            internal bool IsWarning { get; }
        }
    }
}
