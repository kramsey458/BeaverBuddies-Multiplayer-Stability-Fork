using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TimberNet
{
    // Presentation only: chat never enters the replay script, tick ordering, or the state hash.
    public sealed class ChatMessage
    {
        public const string MessageType = "ChatMessage";
        public const string HistoryType = "ChatHistory";
        public const int MaxTextLength = 200;
        /// <summary>How many messages one history frame carries, so a long history goes out as a few modest frames.</summary>
        public const int MaxHistoryBatch = 100;
        // A peer's text is cleaned and shortened, but never accepted at any length: this bounds the work.
        const int MaxRawTextLength = 1024;
        const int MaxHistoryEntries = 200;
        const string FallbackColor = "FFFFFF";

        /// <summary>The host's running number for this message; 0 until the host has stamped it.</summary>
        public int Sequence { get; }
        public int PlayerId { get; }
        public string Name { get; }
        /// <summary>The sender's chosen color, six hex digits.</summary>
        public string Color { get; }
        public string Text { get; }

        public ChatMessage(int sequence, int playerId, string name, string color, string text)
        {
            Sequence = sequence;
            PlayerId = playerId;
            Name = PlayerActivity.CleanName(name);
            Color = IsHex6(color) ? color.ToUpperInvariant() : FallbackColor;
            Text = CleanText(text);
        }

        /// <summary>The same message with the sequence and sender the host assigned. A peer's own claims are never trusted.</summary>
        public ChatMessage Stamped(int sequence, int playerId) => new ChatMessage(sequence, playerId, Name, Color, Text);

        /// <summary>Builds a message to send, or returns false if nothing is left of the text once it is cleaned.</summary>
        public static bool TryCreate(string? name, string? color, string? text, out ChatMessage? message)
        {
            var created = new ChatMessage(0, 0, name ?? "", color ?? "", text ?? "");
            message = created.Text.Length == 0 ? null : created;
            return message != null;
        }

        public JObject ToJson()
        {
            var json = new JObject { ["type"] = MessageType };
            foreach (var pair in Entry()) json[pair.Key] = pair.Value;
            return json;
        }

        JObject Entry() => new JObject
        {
            ["seq"] = Sequence, ["player"] = PlayerId, ["name"] = Name, ["color"] = Color, ["text"] = Text
        };

        public static bool IsChatType(string? type) => type == MessageType || type == HistoryType;

        /// <summary>
        /// One line of plain text: no markup characters (the panel draws rich text, so a peer must not be
        /// able to inject any), no control or direction-changing characters, runs of spaces collapsed.
        /// </summary>
        public static string CleanText(string? text)
        {
            var clean = new StringBuilder();
            bool space = false;
            foreach (char c in text ?? "")
            {
                if (c == '<' || c == '>' || IsDirectionControl(c)) continue;
                if (char.IsWhiteSpace(c) || c == '\t') { space = clean.Length > 0; continue; }
                if (char.IsControl(c)) continue;
                if (space) { clean.Append(' '); space = false; }
                clean.Append(c);
                if (clean.Length >= MaxTextLength) break;
            }
            // Never cut a character that takes two UTF-16 units in half.
            if (clean.Length > 0 && char.IsHighSurrogate(clean[clean.Length - 1])) clean.Length--;
            return clean.ToString();
        }

        static bool IsDirectionControl(char c) =>
            c == '\u200E' || c == '\u200F' || (c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069');

        static bool IsHex6(string? value) => value != null && value.Length == 6 && value.All(Uri.IsHexDigit);

        // Anything a peer sends is validated before it can reach the game thread.
        public static bool TryParse(JObject message, out ChatMessage? chat)
        {
            chat = null;
            if ((string?)message["type"] != MessageType || message.Count > 6) return false;
            return TryParseEntry(message, out chat);
        }

        static bool TryParseEntry(JObject entry, out ChatMessage? chat)
        {
            chat = null;
            try
            {
                if (entry["seq"]?.Type != JTokenType.Integer || entry["player"]?.Type != JTokenType.Integer) return false;
                int sequence = (int)entry["seq"]!, player = (int)entry["player"]!;
                if (sequence < 0 || player < 0) return false;
                foreach (string key in new[] { "name", "color", "text" })
                    if (entry[key]?.Type != JTokenType.String) return false;
                string name = (string)entry["name"]!, color = (string)entry["color"]!, text = (string)entry["text"]!;
                if (name.Length > 64 || text.Length > MaxRawTextLength || !IsHex6(color)) return false;
                var parsed = new ChatMessage(sequence, player, name, color, text);
                if (parsed.Text.Length == 0) return false;
                chat = parsed;
                return true;
            }
            catch (Exception e) when (e is OverflowException || e is InvalidCastException || e is FormatException)
            { return false; }
        }

        /// <summary>Splits a history into frames of at most <see cref="MaxHistoryBatch"/> messages, oldest first.</summary>
        public static IEnumerable<JObject> HistoryFrames(IReadOnlyList<ChatMessage> history)
        {
            for (int start = 0; start < history.Count; start += MaxHistoryBatch)
            {
                var entries = new JArray();
                foreach (var message in history.Skip(start).Take(MaxHistoryBatch)) entries.Add(message.Entry());
                yield return new JObject { ["type"] = HistoryType, ["messages"] = entries };
            }
        }

        /// <summary>A history frame is all or nothing: one bad entry rejects the whole frame.</summary>
        public static bool TryParseHistory(JObject frame, out List<ChatMessage> messages)
        {
            messages = new List<ChatMessage>();
            try
            {
                if ((string?)frame["type"] != HistoryType || frame.Count > 2
                    || !(frame["messages"] is JArray array) || array.Count > MaxHistoryEntries) return false;
                foreach (JToken token in array)
                {
                    if (!(token is JObject entry) || entry.Count > 5 || !TryParseEntry(entry, out ChatMessage? parsed) || parsed == null)
                    { messages.Clear(); return false; }
                    messages.Add(parsed);
                }
                return true;
            }
            catch (Exception e) when (e is OverflowException || e is InvalidCastException || e is FormatException)
            { messages.Clear(); return false; }
        }
    }

    /// <summary>
    /// Every message of a session, oldest first. The host writes it as messages are published and a guest as they
    /// arrive; the game thread reads it. Sequence numbers only ever go up, so a message that arrives twice (in the
    /// history and again live) is kept once.
    /// </summary>
    public sealed class ChatLog
    {
        /// <summary>The most messages kept. A session that goes past it drops the oldest everywhere.</summary>
        public const int MaxMessages = 2000;

        readonly object gate = new object();
        readonly List<ChatMessage> messages = new List<ChatMessage>();
        int lastSequence;

        public int LastSequence { get { lock (gate) return lastSequence; } }
        public int Count { get { lock (gate) return messages.Count; } }

        /// <summary>Adds a message. False if its sequence is not newer than the last one (a duplicate or a stale frame).</summary>
        public bool Add(ChatMessage message)
        {
            lock (gate)
            {
                if (message.Sequence <= lastSequence) return false;
                messages.Add(message);
                lastSequence = message.Sequence;
                if (messages.Count > MaxMessages) messages.RemoveRange(0, messages.Count - MaxMessages);
                return true;
            }
        }

        /// <summary>Up to <paramref name="max"/> messages newer than <paramref name="afterSequence"/>, oldest first.</summary>
        public ChatMessage[] Since(int afterSequence, int max = int.MaxValue)
        {
            lock (gate)
            {
                int start = messages.Count;
                while (start > 0 && messages[start - 1].Sequence > afterSequence) start--;
                int count = Math.Min(max, messages.Count - start);
                return count <= 0 ? Array.Empty<ChatMessage>() : messages.GetRange(start, count).ToArray();
            }
        }

        public ChatMessage[] All() => Since(0);
    }

    /// <summary>
    /// How fast the host lets one guest talk: a short burst, then a steady rate. Thread-safe. Messages over the
    /// limit are dropped, so one player cannot fill everyone's screen or the host's memory.
    /// </summary>
    public sealed class ChatRateLimiter
    {
        readonly object gate = new object();
        readonly double burst, perSecond;
        double tokens, lastMs;
        bool started;

        public ChatRateLimiter(int burst = 6, double perSecond = 2)
        {
            this.burst = burst; this.perSecond = perSecond;
        }

        public bool TryTake(double nowMs)
        {
            lock (gate)
            {
                if (!started) { started = true; tokens = burst; lastMs = nowMs; }
                tokens = Math.Min(burst, tokens + Math.Max(0, nowMs - lastMs) / 1000.0 * perSecond);
                lastMs = nowMs;
                if (tokens < 1) return false;
                tokens -= 1;
                return true;
            }
        }
    }
}
