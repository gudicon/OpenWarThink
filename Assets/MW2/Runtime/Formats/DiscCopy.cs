using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace MW2.Formats
{
    /// <summary>
    /// Installation adapter, not part of the original program: builds a game folder from the MechWarrior 2: Mercenaries
    /// Titanium CD (volume TITAN_MERCS) without running its InstallShield setup. The disc keeps the installed files
    /// uncompressed: Install\16bit holds the 16-bit colour build (MW2.PRJ, DATABASE.MW2, mercsw.dll, MW2SHELL.DLL,
    /// SKYGND.PAR, Missions, ...), Install\data the shared files (SHELLFIX.MW2, the key maps, SMK, SND, Mek, RMG, ...),
    /// and the disc root the SMK movies, KEATING and LAUNCH. A folder made from them matches an installed game file for
    /// file, except for files the player or a patch changed later. The 8-bit build in Install\8bit is not used.
    /// The disc is only read; the copy goes to a folder under the user data folder.
    /// </summary>
    public sealed class DiscCopy
    {
        /// <summary>Sources in order of priority; a later source does not replace a file an earlier one gave.</summary>
        static readonly (string from, string to)[] Layout =
        {
            ("Install/16bit", ""),
            ("Install/data", ""),
            ("Smk", "SMK"),
            ("KEATING", "KEATING"),
            ("LAUNCH", "LAUNCH"),
        };

        const string Marker = "openwarthink-disc.txt";

        /// <summary>True when the folder is the root of a Titanium Mercenaries disc rather than a game folder.</summary>
        public static bool IsDisc(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
            string build = Find(root, "Install/16bit");
            return build != null && FindFile(build, "MW2.PRJ") != null && FindFile(build, "mercsw.dll") != null
                && Find(root, "Install/data") != null;
        }

        /// <summary>True when the target already holds a finished copy of a disc.</summary>
        public static bool IsInstalled(string target) => File.Exists(Path.Combine(target, Marker));

        readonly string _disc, _target;
        Thread _thread;
        long _done, _total;
        volatile string _error;
        volatile bool _finished;

        public DiscCopy(string disc, string target)
        {
            _disc = Path.GetFullPath(disc);
            _target = Path.GetFullPath(target);
        }

        public string Target => _target;
        public bool Finished => _finished;
        public string Error => _error;
        public float Progress => _total <= 0 ? 0f : (float)Interlocked.Read(ref _done) / _total;

        /// <summary>Copies on a background thread; poll Finished and Error.</summary>
        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "MW2 disc copy" };
            _thread.Start();
        }

        /// <summary>Copies on the calling thread; check Error afterwards.</summary>
        public void RunNow() => Run();

        void Run()
        {
            string staging = _target + ".partial";
            try
            {
                var files = Plan();
                foreach (var f in files) _total += new FileInfo(f.source).Length;
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                var buffer = new byte[1 << 20];
                foreach (var (source, relative) in files)
                {
                    string copy = Path.Combine(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy));
                    using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write))
                    {
                        int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, n);
                            Interlocked.Add(ref _done, n);
                        }
                    }
                    // Files on a CD are read only; the copy must not be, so that it can be replaced later.
                    File.SetAttributes(copy, FileAttributes.Normal);
                }
                File.WriteAllText(Path.Combine(staging, Marker),
                    "Copied from the MechWarrior 2: Mercenaries Titanium disc at " + _disc + Environment.NewLine);
                if (Directory.Exists(_target)) Directory.Delete(_target, true);
                Directory.Move(staging, _target);
            }
            catch (Exception e)
            {
                _error = e.Message;
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception) { }
            }
            finally { _finished = true; }
        }

        /// <summary>The files to copy, with the path of each copy inside the game folder.</summary>
        List<(string source, string relative)> Plan()
        {
            if (!IsDisc(_disc)) throw new DirectoryNotFoundException("Not a Mercenaries Titanium disc: " + _disc);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<(string, string)>();
            foreach (var (from, to) in Layout)
            {
                string dir = Find(_disc, from);
                if (dir == null) continue;
                foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string inner = file.Substring(dir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string relative = to.Length == 0 ? inner : Path.Combine(to, inner);
                    if (taken.Add(relative)) files.Add((file, relative));
                }
            }
            return files;
        }

        /// <summary>A sub-folder by path, ignoring case (the disc spells Smk, a game folder SMK).</summary>
        static string Find(string root, string relative)
        {
            string dir = root;
            foreach (string part in relative.Split('/'))
            {
                string next = null;
                foreach (string d in Directory.GetDirectories(dir))
                    if (string.Equals(Path.GetFileName(d), part, StringComparison.OrdinalIgnoreCase)) { next = d; break; }
                if (next == null) return null;
                dir = next;
            }
            return dir;
        }

        static string FindFile(string dir, string name)
        {
            foreach (string f in Directory.GetFiles(dir))
                if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }
    }
}
