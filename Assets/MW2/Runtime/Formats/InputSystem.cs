using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MW2.Formats
{
    /// <summary>
    /// The sim's input layer, read from the game's files the way mercsw.dll does it.
    ///
    /// Control block (player: DAT_100a7ae8, FUN_10015a38 0x10015a38): the channels of the table at 0x10094628 (0x59
    /// records of 0x20 bytes: name, address, type, initial, min, max, accel shift, output shift). Type 0 is an analog
    /// float, type 1 a byte held while its chord is held, type 2 a byte set on the press of its chord. Every frame
    /// (Input_Frame 0x1002c516) clears the type 1/2 bytes, polls the devices, ORs the discrete
    /// bindings in and integrates the analog sinks (FUN_1002c266 0x1002c266).
    ///
    /// inputw.map (FUN_1002bd90 0x1002bd90 -> FUN_1002b8ce 0x1002b8ce): "channel {" blocks; a discrete channel lists a
    /// chord of up to 8 "+/- device key" lines (all + held, all - released, FUN_1002b84d 0x1002b84d); an analog one starts
    /// with "+/- device axis" (sign) and its chord follows. A discrete name ending in _plus / _minus also creates the
    /// analog control of its base name (or base_delta) without a device. Unknown keys are reported once in a message box
    /// (FUN_1002ca6b 0x1002ca6b, silent for the mouse) and the binding is dropped.
    ///
    /// gamekey.map (FUN_1002bf6c 0x1002bf6c): "COMMAND key[+key]"; the key code is the sum of the tokens of 0x10095500
    /// (ALT 0x400, CTRL 0x100, SHIFT 0x200, F1.. 0xb1.., ...) or tolower of the token's first character; the command id
    /// comes from the table 0x10095148 (5-byte records). The keyboard driver posts the code of a key on its release
    /// (WM_KEYUP / WM_SYSKEYUP, FUN_1002e079 0x1002e079): table 0x10097900 by virtual key or tolower(MapVirtualKey(vk, 2)),
    /// plus the held modifiers; Input_HandleCommands 0x10026391 runs the command through Input_Command 0x100267c0.
    /// </summary>
    public sealed class InputSystem
    {
        public const uint ControlTableVa = 0x10094628, ControlCount = 0x59;
        public const uint ControlBlockVa = 0x100a7ae8;
        public const uint CommandTableVa = 0x10095148, CommandCount = 0xbe;
        public const uint KeyTokenTableVa = 0x10095500, KeyTokenCount = 0x1b;
        public const uint KeyboardNamesVa = 0x10097530, KeyboardKeyCount = 0x79;
        public const uint VirtualKeyTableVa = 0x10097900;
        public const uint MouseAxisNamesVa = 0x1009c5d8, MouseButtonNamesVa = 0x1009c5f0;
        public const int BlockSize = 0xa0;

        [Serializable]
        public sealed class ChannelDef
        {
            public string Name;
            public int Offset;          // address - DAT_100a7ae8
            public int Type;            // 0 analog float, 1 held byte, 2 pressed byte
            public int Initial, Min, Max, AccelShift, OutShift;
        }

        public readonly List<ChannelDef> Channels = new List<ChannelDef>();
        public readonly Dictionary<string, int> CommandIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string token, int value)> KeyTokens = new List<(string, int)>();
        public string[] KeyboardNames = new string[0];
        public string[] MouseAxes = new string[0], MouseButtons = new string[0];
        public readonly ushort[] VirtualKeyCodes = new ushort[256];
        /// <summary>DAT_100ac968: command id by key code (0 = none).</summary>
        public readonly byte[] CommandByCode = new byte[0x800];
        public readonly List<string> Errors = new List<string>();

        // Devices: 0 keyboard, 1 mouse (FUN_1002ad97 0x1002ad97 by name).
        struct ChordKey { public int Device, Bit; public bool Released; }

        sealed class Discrete
        {
            public int Offset;
            public bool Edge;           // +0x6c: type 2, set only on the press
            public bool Menu;           // +0x72: a menu_ channel
            public ChordKey[] Chord;
            public bool Previous;       // +0x6d
        }

        sealed class Sink               // DAT_100a94a0 + i * 0x32
        {
            public int Offset;          // +0: output float
            public bool Delta;          // +4 points at the value itself (_delta channels)
            public int Range;           // +8: max - min
            public int MinFixed;        // +0xc: min << 16
            public int ResetValue;      // +0x10
            public int OutShift;        // +0x14
            public bool Touched;        // +0x15
            public int Velocity;        // +0x16
            public int Value;           // +0x1a
            public int AccelShift;      // +0x1e
            public int Plus = -1, Minus = -1, Reset = -1, Set = -1;     // +0x22.. byte channels
            public int Vel { get => Delta ? Value : Velocity; set { if (Delta) Value = value; else Velocity = value; } }
        }

        sealed class Analog             // DAT_100a7ba0 + i * 0x80
        {
            public int Device = -1, Axis;
            public int Sign = 1;        // +0x10
            public Sink Sink;
            public ChordKey[] Chord = new ChordKey[0];
            public int Last;            // +0xc
            public bool Menu;           // +0x7c
        }

        readonly List<Discrete> _discrete = new List<Discrete>();
        readonly List<Sink> _sinks = new List<Sink>();
        readonly List<Analog> _analog = new List<Analog>();

        /// <summary>The control block bytes (offsets from DAT_100a7ae8).</summary>
        public readonly byte[] Block = new byte[BlockSize];
        /// <summary>The analog floats of the control block, by offset / 4.</summary>
        public readonly float[] Floats = new float[BlockSize / 4];

        public float Float(int offset) => Floats[offset / 4];
        public void SetFloat(int offset, float value) => Floats[offset / 4] = value;

        /// <summary>Name of the extracted table file in a <see cref="TableStore"/>.</summary>
        public const string TableName = "input";

        /// <summary>Reads the tables from mercsw.dll and the two maps from the game folder.</summary>
        public static InputSystem Load(string gameFolder, ControlProfile profile = null) =>
            WithMaps(FromDll(File.ReadAllBytes(Path.Combine(gameFolder, "mercsw.dll"))), gameFolder, profile);

        /// <summary>
        /// What the game uses: the tables from the extracted input.json (written from mercsw.dll on first use) and the
        /// two maps from the game folder, which are the player's own data files and stay there. A
        /// <paramref name="profile"/> lays the player's changed bindings over the two maps (see ControlBindings).
        /// </summary>
        public static InputSystem Get(string gameFolder, TableStore store = null, ControlProfile profile = null)
        {
            if (store == null) store = TableStore.Default;
            var file = store.Extract(TableName, () => InputTableFile.From(FromDll(File.ReadAllBytes(Path.Combine(gameFolder, "mercsw.dll")))));
            if (file.Format != InputTableFile.CurrentFormat)
                throw new InvalidDataException($"{store.PathFor(TableName)} has format {file.Format}, this build reads " +
                    $"{InputTableFile.CurrentFormat}: move it aside (keeping your edits) and run MW2 -> Tables -> Extract missing.");
            return WithMaps(file.ToSystem(), gameFolder, profile);
        }

        /// <summary>The 0xbe raw records of the command table 0x10095148 (FromDll keeps the first id of a name).</summary>
        public readonly List<(string name, int id)> CommandRecords = new List<(string, int)>();

        internal void AddCommand(string name, int id)
        {
            CommandRecords.Add((name, id));
            if (!CommandIds.ContainsKey(name)) CommandIds[name] = id;
        }

        /// <summary>FUN_1002bf6c starts with two fixed entries: key code 0x20 -> 0x67 and 0x171 -> 0x71.</summary>
        internal void FixedCommands()
        {
            CommandByCode[0x20] = 0x67;
            CommandByCode[0x171] = 0x71;
        }

        static InputSystem WithMaps(InputSystem s, string gameFolder, ControlProfile profile = null)
        {
            string[] inputw = ReadMap(gameFolder, "inputw.map");
            if (inputw == null) s.Errors.Add("Can't open input mapping file");
            if (profile != null && profile.Actions.Count > 0)
                inputw = ControlBindings.BuildInputMap(inputw, profile, s.KeyName(ShiftBit), s.KeyName(ControlBit));
            if (inputw != null) s.ParseInputMap(inputw);
            string[] gamekey = ReadMap(gameFolder, "gamekey.map");
            if (gamekey != null) s.ParseGameKeys(gamekey);
            else s.Errors.Add("Can't open gamekey mapping file");
            ControlBindings.ApplyCommands(s, profile);
            return s;
        }

        /// <summary>A map file of the game folder (inputw.map, gamekey.map), or null when it is missing.</summary>
        public static string[] ReadMap(string gameFolder, string name)
        {
            string path = Path.Combine(gameFolder ?? "", name);
            return File.Exists(path) ? File.ReadAllLines(path) : null;
        }

        /// <summary>Keyboard name indices the keyboard driver reports for any Shift / Ctrl (0x1002de98).</summary>
        public const int ShiftBit = 0x76, ControlBit = 0x77;

        /// <summary>The keyboard key name at a bit of the name table 0x10097530 ("" when unnamed).</summary>
        public string KeyName(int bit) => bit >= 0 && bit < KeyboardNames.Length ? KeyboardNames[bit] : "";

        public static InputSystem FromDll(byte[] d)
        {
            var s = new InputSystem();
            for (uint i = 0; i < ControlCount; i++)
            {
                int o = PeImage.FileOffset(d, ControlTableVa + i * 0x20);
                s.Channels.Add(new ChannelDef
                {
                    Name = CString(d, BitConverter.ToUInt32(d, o)),
                    Offset = (int)(BitConverter.ToUInt32(d, o + 4) - ControlBlockVa),
                    Type = BitConverter.ToInt32(d, o + 8),
                    Initial = BitConverter.ToInt32(d, o + 12),
                    Min = BitConverter.ToInt32(d, o + 16),
                    Max = BitConverter.ToInt32(d, o + 20),
                    AccelShift = BitConverter.ToInt32(d, o + 24),
                    OutShift = BitConverter.ToInt32(d, o + 28)
                });
            }
            for (uint i = 0; i < CommandCount; i++)
            {
                int o = PeImage.FileOffset(d, CommandTableVa + i * 5);
                s.AddCommand(CString(d, BitConverter.ToUInt32(d, o)), d[o + 4]);
            }
            for (uint i = 0; i < KeyTokenCount; i++)
            {
                int o = PeImage.FileOffset(d, KeyTokenTableVa + i * 6);
                s.KeyTokens.Add((CString(d, BitConverter.ToUInt32(d, o)), BitConverter.ToInt16(d, o + 4)));
            }
            s.KeyboardNames = ReadNames(d, KeyboardNamesVa, (int)KeyboardKeyCount);
            s.MouseAxes = ReadNames(d, MouseAxisNamesVa, 2);
            s.MouseButtons = ReadNames(d, MouseButtonNamesVa, 3);
            int vk = PeImage.FileOffset(d, VirtualKeyTableVa);
            for (int i = 0; i < 256; i++) s.VirtualKeyCodes[i] = BitConverter.ToUInt16(d, vk + i * 2);
            s.FixedCommands();
            return s;
        }

        static string[] ReadNames(byte[] d, uint va, int count)
        {
            var names = new string[count];
            for (int i = 0; i < count; i++)
            {
                uint p = BitConverter.ToUInt32(d, PeImage.FileOffset(d, va + (uint)(i * 4)));
                names[i] = p == 0 ? "" : CString(d, p);
            }
            return names;
        }

        static string CString(byte[] d, uint va)
        {
            int o = PeImage.FileOffset(d, va), end = o;
            while (end < d.Length && d[end] != 0) end++;
            return Encoding.ASCII.GetString(d, o, end - o);
        }

        ChannelDef Channel(string name) => Channels.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        static int DeviceIndex(string name) =>
            string.Equals(name, "keyboard", StringComparison.OrdinalIgnoreCase) ? 0 :
            string.Equals(name, "mouse", StringComparison.OrdinalIgnoreCase) ? 1 : -1;

        // FUN_1002af67 0x1002af67 / FUN_1002ae48 0x1002ae48: a number is taken as the index, else the name (case-insensitive).
        int Lookup(string[] names, string key)
        {
            if (key.Length > 0 && char.IsDigit(key[0])) return int.TryParse(key, out int n) ? n : 0;
            for (int i = 0; i < names.Length; i++)
                if (names[i].Length > 0 && string.Equals(names[i], key, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        void ChannelError(string key, string device)
        {
            // FUN_1002ca6b 0x1002ca6b: one message box per run, none for the "Mouse" device.
            if (_reported || string.Equals(device, "Mouse", StringComparison.Ordinal)) return;
            _reported = true;
            Errors.Add($"The input channel \"{key}\" on device \"{device}\" does not exist");
        }
        bool _reported;

        static bool ReadLine(string[] lines, ref int i, out string line)
        {
            // FUN_1002b086 0x1002b086: cut at '#' or newline, skip blank lines.
            while (i < lines.Length)
            {
                string raw = lines[i++];
                int hash = raw.IndexOf('#');
                line = hash >= 0 ? raw.Substring(0, hash) : raw;
                if (line.Trim().Length > 0) return true;
            }
            line = null;
            return false;
        }

        // " %1[+-] %s %s"
        static bool ScanBinding(string line, out char sign, out string device, out string key)
        {
            sign = '+'; device = key = null;
            var t = line.Trim();
            if (t.Length == 0 || (t[0] != '+' && t[0] != '-')) return false;
            sign = t[0];
            var parts = t.Substring(1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;
            device = parts[0];
            key = parts[1];
            return true;
        }

        /// <summary>
        /// FUN_1002b6c8 0x1002b6c8: chord lines up to a line starting with '}'. A key that is not found is left out;
        /// the result is false when the last line's key was not found (local_14c == -1), which drops a discrete binding
        /// and stops the whole file for an analog one.
        /// </summary>
        bool ReadChord(string[] lines, ref int i, out ChordKey[] keys)
        {
            var chord = new List<ChordKey>();
            int last = 0;
            keys = null;
            while (ReadLine(lines, ref i, out var line) && line[0] != '}')
            {
                if (!ScanBinding(line, out char sign, out string device, out string key)) { Errors.Add($"input mapping error: line {i}, read error"); return false; }
                if (chord.Count > 7) { Errors.Add($"input mapping error: line {i}, chord too big"); return false; }
                int dev = DeviceIndex(device);
                last = dev == 0 ? Lookup(KeyboardNames, key) : dev == 1 ? Lookup(MouseButtons, key) : -1;
                if (last < 0) { if (dev >= 0 && !(key.Length > 0 && char.IsDigit(key[0]))) ChannelError(key, dev == 1 ? "Mouse" : "Keyboard"); continue; }
                chord.Add(new ChordKey { Device = dev, Bit = last, Released = sign == '-' });
            }
            keys = chord.ToArray();
            return last != -1;
        }

        Sink SinkFor(ChannelDef c)
        {
            var existing = _sinks.Find(s => s.Offset == c.Offset);
            if (existing != null) return existing;
            if (_sinks.Count >= 0x32) { Errors.Add("Too many analog sinks"); return null; }
            var sink = new Sink
            {
                Offset = c.Offset,
                Delta = c.Name.Contains("_delta"),
                Range = c.Max - c.Min,
                MinFixed = c.Min << 16,
                OutShift = c.OutShift,
                AccelShift = c.AccelShift
            };
            sink.ResetValue = ((c.Initial << 16) - sink.MinFixed) * 2 / sink.Range - 0x10000;
            sink.Value = sink.ResetValue;
            string baseName = c.Name.Replace("_delta", "");
            ChannelDef plus = Channel(baseName + "_plus"), minus = Channel(baseName + "_minus");
            ChannelDef set = Channel(c.Name + "_set"), reset = Channel(c.Name + "_reset");
            sink.Plus = plus != null ? plus.Offset : -1;
            sink.Minus = minus != null ? minus.Offset : -1;
            sink.Set = set != null ? set.Offset : -1;
            sink.Reset = reset != null ? reset.Offset : -1;
            _sinks.Add(sink);
            return sink;
        }

        // FUN_1002b50d 0x1002b50d
        Analog AnalogFor(ChannelDef c, int device, int axis)
        {
            foreach (var a in _analog)
                if (a.Sink != null && a.Sink.Offset == c.Offset)
                {
                    if (device < 0) return a;
                    if (a.Device < 0) { a.Device = device; a.Axis = axis; return a; }
                }
            if (_analog.Count >= 0x32) { Errors.Add("Too many analog controls"); return null; }
            var created = new Analog { Device = device, Axis = axis, Sink = SinkFor(c), Menu = c.Name.StartsWith("menu_") };
            _analog.Add(created);
            return created;
        }

        public void ParseInputMap(string[] lines)
        {
            int i = 0;
            while (ReadLine(lines, ref i, out var line))
            {
                var t = line.Trim();
                int brace = t.IndexOf('{');
                string name = (brace >= 0 ? t.Substring(0, brace) : t).Trim().Split(' ', '\t')[0];
                var c = Channel(name);
                if (c == null) { Errors.Add($"input mapping error: line {i}, bad channel \"{name}\""); SkipBlock(lines, ref i); continue; }
                if (c.Type == 0)
                {
                    if (!ReadLine(lines, ref i, out var axisLine) || !ScanBinding(axisLine, out char sign, out string device, out string axisName))
                    { Errors.Add($"input mapping error: line {i}, read error"); SkipBlock(lines, ref i); continue; }
                    int dev = DeviceIndex(device);
                    int axis = dev == 1 ? Lookup(MouseAxes, axisName) : -1;
                    if (axis < 0) { if (dev >= 0) ChannelError(axisName, dev == 1 ? "Mouse" : "Keyboard"); SkipBlock(lines, ref i); continue; }
                    var a = AnalogFor(c, dev, axis);
                    if (a == null) return;
                    a.Sign = sign != '-' ? 1 : -1;
                    if (!ReadChord(lines, ref i, out a.Chord)) return;
                    continue;
                }
                if (!ReadChord(lines, ref i, out var chord)) continue;
                if (_discrete.Count >= 100) { Errors.Add("Too many discrete controls"); return; }
                _discrete.Add(new Discrete { Offset = c.Offset, Edge = c.Type != 1, Menu = c.Name.StartsWith("menu_"), Chord = chord });
                if (c.Type == 1)
                {
                    int cut = c.Name.IndexOf("_plus", StringComparison.Ordinal);
                    if (cut < 0) cut = c.Name.IndexOf("_minus", StringComparison.Ordinal);
                    if (cut >= 0)
                    {
                        string baseName = c.Name.Substring(0, cut);
                        var analog = Channel(baseName) ?? Channel(baseName + "_delta");
                        if (analog != null && analog.Type == 0) AnalogFor(analog, -1, 0);
                    }
                }
            }
        }

        static void SkipBlock(string[] lines, ref int i)
        {
            while (ReadLine(lines, ref i, out var line))
                if (line[0] == '}') return;
        }

        public void ParseGameKeys(string[] lines)
        {
            int i = 0;
            while (ReadLine(lines, ref i, out var line))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) { Errors.Add($"gamekey mapping error: line {i}, read error"); return; }
                int code = 0;
                foreach (var token in parts[1].Split('+'))
                {
                    if (token.Length == 0) continue;
                    int value = 0;
                    foreach (var (name, v) in KeyTokens)
                        if (string.Equals(name, token, StringComparison.OrdinalIgnoreCase)) { value = v; break; }
                    code += value != 0 ? value : char.ToLowerInvariant(token[0]);
                }
                if (!CommandIds.TryGetValue(parts[0], out int id)) { Errors.Add($"gamekey mapping error: line {i}, no command \"{parts[0]}\""); return; }
                if (code < 0 || code >= CommandByCode.Length) continue;
                if (CommandByCode[code] != 0 && CommandByCode[code] != id) { Errors.Add($"gamekey mapping error: line {i}, multiply defined key \"{line.Trim()}\""); return; }
                CommandByCode[code] = (byte)id;
            }
        }

        /// <summary>
        /// FUN_1002e079 0x1002e079: the key code of a released key, from its virtual key (0 when it has none).
        /// <paramref name="unshiftedChar"/> is MapVirtualKey(vk, MAPVK_VK_TO_CHAR) for keys the table lacks.
        /// </summary>
        public int KeyCode(int virtualKey, char unshiftedChar, bool ctrl, bool alt, bool shift)
        {
            int code = virtualKey >= 0 && virtualKey < 256 ? VirtualKeyCodes[virtualKey] : 0;
            if (code == 0) code = char.ToLowerInvariant(unshiftedChar);
            if (code == 0) return 0;
            if (ctrl) code |= 0x100;
            if (alt) code |= 0x400;
            if (shift) code |= 0x200;
            return code;
        }

        public int CommandFor(int keyCode) => keyCode > 0 && keyCode < CommandByCode.Length ? CommandByCode[keyCode] : 0;

        public struct DeviceState
        {
            public Func<int, bool> KeyboardBit;     // bit = key name index (scan code - 1, extended keys remapped)
            public int MouseButtons;                // bit 0 left, 1 middle, 2 right
            public bool MouseInside;                // the pointer is inside the window (FUN_10042b90)
            public int MouseY, MouseX;              // pointer from the window centre, -0x10000..0x10000
        }

        bool ChordHeld(ChordKey[] chord, in DeviceState dev)
        {
            foreach (var k in chord)
            {
                bool down = k.Device == 0 ? dev.KeyboardBit(k.Bit) : k.Device == 1 && (dev.MouseButtons & (1 << k.Bit)) != 0;
                if (k.Released ? down : !down) return false;
            }
            return true;
        }

        /// <summary>
        /// Port adapter for bindings changed during a mission: takes over the control block (throttle, torso, toggles
        /// such as reverse) and the analog sink values of the input layer this one replaces, so nothing jumps when the
        /// new bindings take effect.
        /// </summary>
        public void CarryStateFrom(InputSystem old)
        {
            if (old == null) return;
            Array.Copy(old.Block, Block, BlockSize);
            Array.Copy(old.Floats, Floats, Floats.Length);
            foreach (var s in _sinks)
            {
                // The same channel keeps its exact fixed-point value; a sink the old layer lacked starts from its float.
                var before = old._sinks.Find(o => o.Offset == s.Offset);
                s.Value = before != null ? before.Value : ValueFromFloat(s);
                s.Vel = 0;
                s.Touched = false;
            }
        }

        /// <summary>The sink's value from its current float (the _set branch of 0x1002c516 / 0x1002c266).</summary>
        int ValueFromFloat(Sink s)
        {
            int fixedValue = (int)(Floats[s.Offset / 4] * 65536f + 0.5f) << s.OutShift;
            return (int)((fixedValue - (double)s.MinFixed) * 2.0 / s.Range - 65536.0);
        }

        /// <summary>
        /// One input frame of 0x1002c516: analog reset / set, clear the type 1 / 2 bytes, discrete bindings, analog
        /// integration (<paramref name="ticks"/> = DAT_100a6644) and outputs. <paramref name="menuOnly"/> is a lance /
        /// options menu holding the input (DAT_10094610 = 0): only menu_ channels update.
        /// </summary>
        public void Frame(in DeviceState dev, int ticks, bool menuOnly = false)
        {
            foreach (var a in _analog)
            {
                var s = a.Sink;
                if (s.Reset < 0 || Block[s.Reset] == 0)
                {
                    if (s.Set >= 0 && Block[s.Set] != 0)
                    {
                        int v = ValueFromFloat(s);
                        s.Vel = 0;
                        s.Value = v;
                    }
                }
                else
                {
                    s.Value = s.ResetValue;
                    s.Vel = 0;
                    a.Last = 0;
                }
                s.Touched = false;
            }
            foreach (var c in Channels)
                if (c.Type != 0 && c.Offset >= 0 && c.Offset < BlockSize) Block[c.Offset] = 0;
            foreach (var d in _discrete)
            {
                if (menuOnly && !d.Menu) continue;
                bool held = ChordHeld(d.Chord, dev);
                if (!d.Edge || !d.Previous) Block[d.Offset] |= (byte)(held ? 1 : 0);
                d.Previous = held;
            }
            foreach (var a in _analog)
            {
                if (menuOnly && !a.Menu) continue;
                var s = a.Sink;
                bool touched = Integrate(a, ticks);
                if (!touched && a.Device == 1 && dev.MouseInside && ChordHeld(a.Chord, dev))
                {
                    int v = (a.Axis == 0 ? dev.MouseY : dev.MouseX) * a.Sign;
                    if (a.Last == v) { if (s.Value == s.Vel) s.Value = v; }
                    else s.Value = v;
                    a.Last = v;
                }
                int value = Math.Clamp(s.Value, -0x10000, 0x10000);
                Floats[s.Offset / 4] = (((value + 0x10000) * s.Range) / 2 + s.MinFixed >> s.OutShift) * (1f / 65536f);
            }
        }

        // FUN_1002c266 0x1002c266
        bool Integrate(Analog a, int ticks)
        {
            var s = a.Sink;
            bool touched = s.Touched;
            if (s.Reset < 0 || Block[s.Reset] == 0)
            {
                if (s.Set >= 0 && Block[s.Set] != 0)
                {
                    int v = ValueFromFloat(s);
                    s.Vel = 0;
                    s.Value = v;
                    touched = true;
                }
            }
            else
            {
                s.Vel = 0;
                s.Value = s.ResetValue;
                touched = true;
            }
            if (!s.Touched)
            {
                int step = ticks * 3 << s.AccelShift;
                if (s.Plus >= 0 && Block[s.Plus] != 0)
                {
                    if (s.Vel < 0) s.Vel = 0;
                    s.Vel = Math.Min(s.Vel + step, 0x8000);
                    s.Value = Math.Min(s.Value + s.Vel, 0x10000);
                    touched = true;
                }
                if (s.Minus >= 0 && Block[s.Minus] != 0)
                {
                    if (s.Vel > 0) s.Vel = 0;
                    s.Vel = Math.Max(s.Vel - step, -0x8000);
                    s.Value = Math.Max(s.Value + s.Vel, -0x10000);
                    touched = true;
                }
                if (!touched) s.Vel = 0;
            }
            s.Touched = touched;
            return touched;
        }

        /// <summary>
        /// Input_Command 0x100267c0 for the commands that only set control block bytes (or the throttle); returns false
        /// for the others, which the game code handles itself.
        /// </summary>
        public bool ApplyCommand(int command)
        {
            int offset;
            switch (command)
            {
                case 0x01:      // RESET_INPUTS
                    foreach (int o in new[] { 0x18, 0x1c, 0x72, 0x75, 0x7c, 0x7f }) Block[o] = 1;
                    return true;
                case 0x29: offset = 0x3b; break;    // INSPECT_TARGET (DAT_100a7b23)
                case 0x2e: offset = 0x88; break;    // COCKPIT_RESET_ZOOM (DAT_100a7b70)
                case 0x3b: offset = 0x3d; break;    // NEXT/PREV/RESET_GAMETHING
                case 0x3c: offset = 0x3e; break;
                case 0x3d: offset = 0x3f; break;
                case 0x3f: offset = 0x31; break;    // NEXT/PREV/RESET_NAVPOINT
                case 0x40: offset = 0x32; break;
                case 0x41: offset = 0x33; break;
                case 0x42: offset = 0x40; break;    // NEXT/PREV/RESET_GAMEPIECE
                case 0x43: offset = 0x41; break;
                case 0x44: offset = 0x42; break;
                case 0x56: Block[0x30] = (byte)(Block[0x30] != 0 ? 0 : 1); return true;    // REVERSE_DIRECTION (DAT_100a7b18)
                case 0x61: offset = 0x43; break;    // TOGGLE_AUTOPILOT
                case 0x62: offset = 0x2c; break;    // TOGGLE_GROUP_FIRE
                case 0x63: offset = 0x2d; break;    // FIRE_ALL_WEAPONS
                case 0x64: offset = 0x34; break;    // NEXT/PREV/RESET_TARGET
                case 0x65: offset = 0x35; break;
                case 0x66: offset = 0x36; break;
                case 0x68: offset = 0x39; break;    // TARGET_NEAREST_ENEMY
                case 0x69: offset = 0x38; break;    // TARGET_FRIENDLY
                case 0x6a: offset = 0x37; break;    // TARGET_AT_RETICLE
                case 0x6b: offset = 0x3a; break;    // TARGET_LAST_SHOT
                case 0x6c: offset = 0x3c; break;    // NEXT_OBJECTIVE
                case 0x6f: offset = 0x44; break;    // SELF_DESTRUCT
                case 0xb3: offset = 0x2b; break;    // NEXT_WEAPON_GROUP
                case 0xb4: offset = 0x27; break;    // FIRE_WEAPON_GROUP
                case 0xb5: offset = 0x28; break;    // weapon groups 1..3
                case 0xb6: offset = 0x29; break;
                case 0xb7: offset = 0x2a; break;
                case 0xc6: Block[0x27] = 1; Block[0x2b] = 1; return true;     // FIRE_CYCLE_WEAPON_GROUP
                default:
                    if (command >= 0x31 && command <= 0x3a)
                    {
                        // THROTTLE_STOP..THROTTLE_FULL: throttle float = (command - 0x31) * _DAT_100872d4 (1/576), set byte.
                        Floats[0x08 / 4] = (command - 0x31) * 0.0017361111f;
                        Block[0x24] = 1;
                        return true;
                    }
                    return false;
            }
            Block[offset] = 1;
            return true;
        }
    }
    /// <summary>A record of the command table 0x10095148: command name and id.</summary>
    [Serializable]
    public sealed class InputCommandDef
    {
        public string Name;
        public int Id;
    }

    /// <summary>A key token of 0x10095500 ("ALT" 0x400, "F1" 0xb1, ...).</summary>
    [Serializable]
    public sealed class InputKeyTokenDef
    {
        public string Token;
        public int Value;
    }

    /// <summary>
    /// The input tables of mercsw.dll as the extracted input.json: the control channels (0x10094628, Offset from the
    /// control block DAT_100a7ae8), the command table, the key tokens, the keyboard / mouse names and the virtual-key
    /// table 0x10097900. inputw.map and gamekey.map are not part of it: they stay the player's files in the game folder.
    /// </summary>
    [Serializable]
    public sealed class InputTableFile
    {
        public const int CurrentFormat = 1;
        public int Format = CurrentFormat;
        public List<InputSystem.ChannelDef> Channels = new List<InputSystem.ChannelDef>();
        public List<InputCommandDef> Commands = new List<InputCommandDef>();
        public List<InputKeyTokenDef> KeyTokens = new List<InputKeyTokenDef>();
        public string[] KeyboardNames = new string[0];
        public string[] MouseAxes = new string[0], MouseButtons = new string[0];
        public int[] VirtualKeyCodes = new int[256];

        public static InputTableFile From(InputSystem s)
        {
            var f = new InputTableFile();
            f.Channels.AddRange(s.Channels);
            foreach (var c in s.CommandRecords) f.Commands.Add(new InputCommandDef { Name = c.name, Id = c.id });
            foreach (var k in s.KeyTokens) f.KeyTokens.Add(new InputKeyTokenDef { Token = k.token, Value = k.value });
            f.KeyboardNames = (string[])s.KeyboardNames.Clone();
            f.MouseAxes = (string[])s.MouseAxes.Clone();
            f.MouseButtons = (string[])s.MouseButtons.Clone();
            for (int i = 0; i < 256; i++) f.VirtualKeyCodes[i] = s.VirtualKeyCodes[i];
            return f;
        }

        public InputSystem ToSystem()
        {
            var s = new InputSystem();
            s.Channels.AddRange(Channels);
            foreach (var c in Commands) s.AddCommand(c.Name ?? "", c.Id);
            foreach (var k in KeyTokens) s.KeyTokens.Add((k.Token ?? "", k.Value));
            s.KeyboardNames = (string[])KeyboardNames.Clone();
            s.MouseAxes = (string[])MouseAxes.Clone();
            s.MouseButtons = (string[])MouseButtons.Clone();
            for (int i = 0; i < 256 && i < VirtualKeyCodes.Length; i++) s.VirtualKeyCodes[i] = (ushort)VirtualKeyCodes[i];
            s.FixedCommands();
            return s;
        }
    }
}
