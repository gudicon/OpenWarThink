using System;
using System.Collections.Generic;
using System.Linq;

namespace MW2.Formats
{
    /// <summary>
    /// The player's key bindings, saved as a <see cref="ControlProfile"/> and laid over the game's own inputw.map and
    /// gamekey.map before <see cref="InputSystem"/> reads them.
    ///
    /// Port extension, not original behaviour. The original edits inputw.map only through its Cockpit Controls screen
    /// (FUN_1000f864), for a fixed set of choices, and gamekey.map only by hand. Here every action can take any number
    /// of keyboard keys (with Shift / Ctrl) or mouse buttons, and every command any keyboard key with Shift / Ctrl / Alt.
    /// The input layer itself is unchanged: cockpit actions become ordinary inputw.map blocks for the original parser
    /// (FUN_1002b8ce 0x1002b8ce), commands become entries of the key-code table DAT_100ac968.
    ///
    /// One action often drives several channels with the same chord in the shipped map (a jump direction is the
    /// direction channel plus jumpjet_enabled, the centre key resets four channels), so bindings are made per action,
    /// not per channel. A shipped chord belongs to the action whose channel set it drives exactly; chords that match
    /// no action (mouse axes, unusual combinations) are left as they are.
    /// </summary>
    public static class ControlBindings
    {
        public sealed class ControlAction
        {
            public readonly string Id, Group, Label;
            public readonly string[] Channels;
            public ControlAction(string id, string group, string label, params string[] channels)
            { Id = id; Group = group; Label = label; Channels = channels; }
        }

        /// <summary>Cockpit actions shown to the player, in display order.</summary>
        public static readonly ControlAction[] Actions =
        {
            new ControlAction("throttle_up", "Movement", "Throttle up", "throttle_plus"),
            new ControlAction("throttle_down", "Movement", "Throttle down", "throttle_minus"),
            new ControlAction("turn_left", "Movement", "Turn left", "legs_pan_minus"),
            new ControlAction("turn_right", "Movement", "Turn right", "legs_pan_plus"),
            new ControlAction("jump", "Jump jets", "Jump", "jumpjet_enabled"),
            new ControlAction("jump_forward", "Jump jets", "Jump forward", "jumpjet_fire_forward", "jumpjet_enabled"),
            new ControlAction("jump_backward", "Jump jets", "Jump backward", "jumpjet_fire_backward", "jumpjet_enabled"),
            new ControlAction("jump_left", "Jump jets", "Jump left", "jumpjet_fire_left", "jumpjet_enabled"),
            new ControlAction("jump_right", "Jump jets", "Jump right", "jumpjet_fire_right", "jumpjet_enabled"),
            new ControlAction("torso_left", "Torso", "Twist torso left", "torso_pan_minus"),
            new ControlAction("torso_right", "Torso", "Twist torso right", "torso_pan_plus"),
            new ControlAction("torso_up", "Torso", "Torso pitch up", "torso_tilt_plus"),
            new ControlAction("torso_down", "Torso", "Torso pitch down", "torso_tilt_minus"),
            new ControlAction("centre", "Torso", "Centre torso and view",
                "torso_tilt_reset", "pilot_tilt_reset", "torso_pan_reset", "pilot_pan_reset"),
            new ControlAction("fire", "Weapons", "Fire", "weapon_fire"),
            new ControlAction("next_weapon", "Weapons", "Next weapon", "weapon_cycle"),
            new ControlAction("fire_group", "Weapons", "Fire selected group", "weapon_fire_group"),
            new ControlAction("fire_group_1", "Weapons", "Fire group 1", "weapon_fire_group_1"),
            new ControlAction("fire_group_2", "Weapons", "Fire group 2", "weapon_fire_group_2"),
            new ControlAction("fire_group_3", "Weapons", "Fire group 3", "weapon_fire_group_3"),
            new ControlAction("next_group", "Weapons", "Next weapon group", "weapon_cycle_group"),
            new ControlAction("group_fire", "Weapons", "Toggle group / chain fire", "toggle_group_fire"),
            new ControlAction("target_reticle", "Targeting", "Target at reticle", "target_reticle"),
            new ControlAction("look_left", "View", "Look left / orbit left", "pilot_pan_minus", "eyepoint_pan_minus"),
            new ControlAction("look_right", "View", "Look right / orbit right", "pilot_pan_plus", "eyepoint_pan_plus"),
            new ControlAction("look_up", "View", "Look up / camera higher", "pilot_tilt_plus", "track_height_plus"),
            new ControlAction("look_down", "View", "Look down / camera lower", "pilot_tilt_minus", "track_height_minus"),
            new ControlAction("glance_left", "View", "Glance left", "glance_left", "eyepoint_pan_minus"),
            new ControlAction("glance_right", "View", "Glance right", "glance_right", "eyepoint_pan_plus"),
            new ControlAction("zoom_in", "View", "Zoom in / camera closer", "zoom_factor_plus", "track_distance_minus"),
            new ControlAction("zoom_out", "View", "Zoom out / camera farther", "zoom_factor_minus", "track_distance_plus"),
        };

        public static ControlAction Action(string id) => Array.Find(Actions, a => a.Id == id);

        static HashSet<string> _managed;
        static HashSet<string> Managed => _managed ??= new HashSet<string>(
            Actions.SelectMany(a => a.Channels), StringComparer.OrdinalIgnoreCase);

        // ---- chords ----

        /// <summary>One chord line as a token: sign, device and key name, e.g. "+keyboard:GreyLeftArrow".</summary>
        public static string Token(char sign, string device, string key) =>
            sign + device.ToLowerInvariant() + ":" + key;

        /// <summary>A chord in canonical form: its tokens sorted and joined with spaces.</summary>
        public static string Chord(IEnumerable<string> tokens) =>
            string.Join(" ", tokens.Distinct().OrderBy(t => t, StringComparer.Ordinal));

        public static string[] Tokens(string chord) =>
            chord.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        public static bool ParseToken(string token, out char sign, out string device, out string key)
        {
            sign = '+'; device = key = null;
            if (token.Length < 3 || (token[0] != '+' && token[0] != '-')) return false;
            int colon = token.IndexOf(':');
            if (colon < 2 || colon == token.Length - 1) return false;
            sign = token[0];
            device = token.Substring(1, colon - 1);
            key = token.Substring(colon + 1);
            return true;
        }

        // ---- inputw.map blocks ----

        sealed class Block
        {
            public string Channel;
            public readonly List<string> Lines = new List<string>();    // raw lines between the braces
            public string Chord;                                         // canonical chord, null if not a plain chord
        }

        // The same line rules as InputSystem.ReadLine (FUN_1002b086): cut at '#', skip blank lines.
        static List<Block> Blocks(string[] lines)
        {
            var blocks = new List<Block>();
            Block cur = null;
            foreach (string raw in lines)
            {
                int hash = raw.IndexOf('#');
                string line = (hash >= 0 ? raw.Substring(0, hash) : raw).Trim();
                if (line.Length == 0) continue;
                if (cur == null)
                {
                    int brace = line.IndexOf('{');
                    string name = (brace >= 0 ? line.Substring(0, brace) : line).Trim().Split(' ', '\t')[0];
                    cur = new Block { Channel = name };
                    continue;
                }
                if (line[0] == '}') { cur.Chord = ChordOf(cur.Lines); blocks.Add(cur); cur = null; continue; }
                cur.Lines.Add(line);
            }
            if (cur != null) { cur.Chord = ChordOf(cur.Lines); blocks.Add(cur); }
            return blocks;
        }

        static string ChordOf(List<string> lines)
        {
            var tokens = new List<string>();
            foreach (var line in lines)
            {
                var parts = line.Substring(1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if ((line[0] != '+' && line[0] != '-') || parts.Length < 2) return null;
                tokens.Add(Token(line[0], parts[0], parts[1]));
            }
            return tokens.Count > 0 ? Chord(tokens) : null;
        }

        /// <summary>For each shipped chord of a managed channel, the action whose channels it drives exactly.</summary>
        static Dictionary<string, ControlAction> Owners(List<Block> blocks)
        {
            var channels = new Dictionary<string, HashSet<string>>();
            foreach (var b in blocks)
            {
                if (b.Chord == null || !Managed.Contains(b.Channel)) continue;
                if (!channels.TryGetValue(b.Chord, out var set))
                    channels[b.Chord] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(b.Channel);
            }
            var owners = new Dictionary<string, ControlAction>();
            foreach (var kv in channels)
            {
                var action = Array.Find(Actions, a => a.Channels.Length == kv.Value.Count && a.Channels.All(kv.Value.Contains));
                if (action != null) owners[kv.Key] = action;
            }
            return owners;
        }

        /// <summary>The chords the shipped inputw.map gives each action, in file order.</summary>
        public static Dictionary<string, List<string>> DefaultChords(string[] inputMap)
        {
            var result = Actions.ToDictionary(a => a.Id, a => new List<string>());
            if (inputMap == null) return result;
            var blocks = Blocks(inputMap);
            var owners = Owners(blocks);
            foreach (var b in blocks)
                if (b.Chord != null && owners.TryGetValue(b.Chord, out var a) && !result[a.Id].Contains(b.Chord))
                    result[a.Id].Add(b.Chord);
            return result;
        }

        /// <summary>
        /// The inputw.map the input layer reads: the shipped file without the chords of the actions the profile
        /// changes, plus a block per changed action, chord and channel. A chord without Shift (or Ctrl) whose keys are
        /// also bound together with Shift (Ctrl) gets "- keyboard Shift" ("Control"), the way the shipped map keeps
        /// Z apart from Shift+Z.
        /// </summary>
        public static string[] BuildInputMap(string[] inputMap, ControlProfile profile, string shiftName = "Shift", string controlName = "Control")
        {
            inputMap ??= new string[0];
            if (profile == null || profile.Actions.Count == 0) return inputMap;
            var changed = new Dictionary<string, List<string>>();
            foreach (var e in profile.Actions)
                if (Action(e.Id) != null) changed[e.Id] = e.Chords.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            if (changed.Count == 0) return inputMap;

            var blocks = Blocks(inputMap);
            var owners = Owners(blocks);
            var defaults = DefaultChords(inputMap);
            var effective = new Dictionary<string, List<string>>();
            foreach (var a in Actions) effective[a.Id] = changed.TryGetValue(a.Id, out var c) ? c : defaults[a.Id];

            var output = new List<string> { "# Player bindings over the shipped inputw.map (OpenWarThink controls)" };
            foreach (var b in blocks)
            {
                // Drop only the shipped chords of changed actions; everything else stays as shipped.
                if (b.Chord != null && owners.TryGetValue(b.Chord, out var owner) && changed.ContainsKey(owner.Id)) continue;
                output.Add(b.Channel + " {");
                foreach (var line in b.Lines) output.Add("\t" + line);
                output.Add("}");
            }

            var all = effective.Values.SelectMany(v => v).ToList();
            foreach (var a in Actions)
            {
                if (!changed.ContainsKey(a.Id)) continue;
                foreach (string chord in effective[a.Id])
                {
                    var tokens = WithExclusions(chord, all, shiftName, controlName);
                    foreach (string channel in a.Channels)
                    {
                        output.Add(channel + " {");
                        foreach (string t in tokens)
                            if (ParseToken(t, out char sign, out string device, out string key))
                                output.Add("\t" + sign + " " + device + "\t" + key);
                        output.Add("}");
                    }
                }
            }
            return output.ToArray();
        }

        static List<string> WithExclusions(string chord, List<string> all, string shiftName, string controlName)
        {
            var tokens = Tokens(chord).ToList();
            string shift = Token('+', "keyboard", shiftName), control = Token('+', "keyboard", controlName);
            var keys = Keys(tokens, shift, control);
            foreach (string mod in new[] { shift, control })
            {
                string name = mod.Substring(mod.IndexOf(':') + 1);
                if (tokens.Any(t => t.EndsWith(":" + name, StringComparison.Ordinal))) continue;
                foreach (string other in all)
                {
                    var o = Tokens(other);
                    if (o.Contains(mod) && Keys(o, shift, control).SetEquals(keys))
                    {
                        tokens.Add("-" + mod.Substring(1));
                        break;
                    }
                }
            }
            return tokens;
        }

        static HashSet<string> Keys(IEnumerable<string> tokens, string shift, string control) =>
            new HashSet<string>(tokens.Where(t => t[0] == '+' && t != shift && t != control));

        // ---- gamekey.map commands ----

        /// <summary>The command names of a gamekey.map, in file order, each once.</summary>
        public static List<string> CommandNames(string[] gameKeys)
        {
            var names = new List<string>();
            if (gameKeys == null) return names;
            foreach (string raw in gameKeys)
            {
                int hash = raw.IndexOf('#');
                var parts = (hash >= 0 ? raw.Substring(0, hash) : raw).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && !names.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) names.Add(parts[0]);
            }
            return names;
        }

        /// <summary>The key codes bound to a command (DAT_100ac968 entries with its id).</summary>
        public static List<int> CommandCodes(InputSystem s, string name)
        {
            var codes = new List<int>();
            if (!s.CommandIds.TryGetValue(name, out int id)) return codes;
            for (int code = 1; code < s.CommandByCode.Length; code++)
                if (s.CommandByCode[code] == id) codes.Add(code);
            return codes;
        }

        /// <summary>Replaces the key codes of each command the profile changes.</summary>
        public static void ApplyCommands(InputSystem s, ControlProfile profile)
        {
            if (profile == null) return;
            foreach (var e in profile.Commands)
            {
                if (!s.CommandIds.TryGetValue(e.Name, out int id) || id <= 0 || id > 255) continue;
                for (int code = 0; code < s.CommandByCode.Length; code++)
                    if (s.CommandByCode[code] == id) s.CommandByCode[code] = 0;
                foreach (int code in e.Codes)
                    if (code > 0 && code < s.CommandByCode.Length) s.CommandByCode[code] = (byte)id;
            }
        }
    }

    /// <summary>The player's changes to the shipped controls (controls.json in the user-data folder).</summary>
    [Serializable]
    public sealed class ControlProfile
    {
        public const int CurrentFormat = 1;
        public int Format = CurrentFormat;
        public List<ActionBinding> Actions = new List<ActionBinding>();
        public List<CommandBinding> Commands = new List<CommandBinding>();

        [Serializable]
        public sealed class ActionBinding
        {
            public string Id;
            public List<string> Chords = new List<string>();
        }

        [Serializable]
        public sealed class CommandBinding
        {
            public string Name;
            public List<int> Codes = new List<int>();
        }

        public ActionBinding FindAction(string id) => Actions.Find(a => a.Id == id);
        public CommandBinding FindCommand(string name) =>
            Commands.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        public ControlProfile Clone() => new ControlProfile
        {
            Format = Format,
            Actions = Actions.Select(a => new ActionBinding { Id = a.Id, Chords = new List<string>(a.Chords) }).ToList(),
            Commands = Commands.Select(c => new CommandBinding { Name = c.Name, Codes = new List<int>(c.Codes) }).ToList()
        };
    }
}
