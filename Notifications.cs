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
    /// Short lived on screen messages, so toggling a mod doesn't mean alt tabbing to read
    /// the console.
    /// </summary>
    internal sealed class Notifications
    {
        /// <summary>How long a message spends fading out at the end of its life.</summary>
        private const float FadeSeconds = 0.6f;

        /// <summary>Gap between stacked messages, in pixels.</summary>
        private const float LineSpacing = 4f;

        /// <summary>Distance from the screen edge, in pixels.</summary>
        private const float Margin = 24f;

        /// <summary>Older messages are dropped once this many are queued.</summary>
        private const int MaxVisible = 4;

        private readonly List<Message> messages = new();
        private GUIStyle style;
        private int styleFontSize;

        /// <summary>Queues a message. Ignored when notifications are switched off.</summary>
        internal void Show(string text)
        {
            if (!Plugin.Options.ShowNotifications.Value || string.IsNullOrEmpty(text))
            {
                return;
            }

            messages.Add(new Message(text, Time.unscaledTime + Plugin.Options.NotificationSeconds.Value));

            if (messages.Count > MaxVisible)
            {
                messages.RemoveRange(0, messages.Count - MaxVisible);
            }
        }

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

        /// <summary>Draws the queue. Must be called from OnGUI.</summary>
        internal void Draw()
        {
            if (messages.Count == 0 || !Plugin.Options.ShowNotifications.Value)
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

            for (int i = 0; i < messages.Count; i++)
            {
                Message message = messages[i];

                float remaining = message.Expiry - Time.unscaledTime;
                float alpha = Mathf.Clamp01(remaining / FadeSeconds);

                float offset = i * (lineHeight + LineSpacing);
                float y = fromTop
                    ? Margin + offset
                    : Screen.height - Margin - lineHeight - offset;

                var area = new Rect(Margin, y, Screen.width - (Margin * 2f), lineHeight);

                // Drawn twice, offset by a pixel, so the text stays readable against a
                // bright skybox as well as a dark corridor.
                GUI.color = new Color(0f, 0f, 0f, alpha * 0.8f);
                GUI.Label(new Rect(area.x + 2f, area.y + 2f, area.width, area.height), message.Text, current);

                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.Label(area, message.Text, current);
            }

            GUI.color = previous;
        }

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
            internal Message(string text, float expiry)
            {
                Text = text;
                Expiry = expiry;
            }

            internal string Text { get; }

            internal float Expiry { get; }
        }
    }
}
