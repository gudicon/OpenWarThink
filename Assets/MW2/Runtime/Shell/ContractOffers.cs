using System;
using System.Collections.Generic;
using System.Text;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>
    /// The offers of the contracts screen (FUN_1000a530 0x1000a530 with Contract_Read FUN_1006b2e3 0x1006b2e3). Each
    /// step of the list (SLST after a success, FLST after a failure) is read from MW2.PRJ (a name starting with "RND"
    /// from beside the game): CPGN +0x88 the employer, CTNT the text. A RAN step runs the random mission generator
    /// (FUN_1006adab) with the step's pay (CPGN +0x5c / +0x60) and the fourth letter's number, then shows the generated
    /// contract RND&lt;n&gt;CAM1 instead. Seeds: on the first visit of the step (+0x23b8 = 0) the first RAN offer takes a
    /// new seed from the tick counter, the following ones a rand() seed, and each offer's seed goes into +0x2342
    /// (-1 without one); on a later visit every offer replays its seed with srand(). After the story (+0x242f) three
    /// generated offers "RAN1cam1" follow, their seeds given as bytes of rand() values (as the original passes them).
    /// </summary>
    public static class ContractOffers
    {
        public sealed class Offer
        {
            public string Step, Employer = "", Text;
            /// <summary>Offer +8: the generator's seed (-1 none).</summary>
            public int Seed = -1;
            /// <summary>DAT_100c4ad8: generated for the story's end (+0x2433 on acceptance).</summary>
            public bool Generated;
        }

        /// <summary>FUN_1000a530. <paramref name="tick"/> is DAT_100cd478; a failed generator run is reported through
        /// <paramref name="rmgError"/> (FUN_1006ac42).</summary>
        public static List<Offer> Read(Career career, ShellData data, int tick, Action<string> rmgError = null)
        {
            if (career == null) throw new ArgumentNullException(nameof(career));
            if (data == null) throw new ArgumentNullException(nameof(data));
            var result = new List<Offer>();
            bool tickSeed = true, newSeed = true;      // local_20, local_1c
            if (!career.Endless)
            {
                bool success = career.Outcome239C;
                int count = Math.Min(success ? career.SuccessCount : career.FailCount, 10);
                for (int i = 0; i < count; i++)
                {
                    if (career.Byte23B8)
                    {
                        if (career.OfferSeed(i) >= 0) data.Random.Srand((uint)career.OfferSeed(i));
                        tickSeed = newSeed = false;
                    }
                    int at = (success ? 0x202 : 0x2a2) + i * 0x10;
                    var offer = new Offer();
                    tickSeed = Read(career, data, at, offer, tickSeed, newSeed, tick, rmgError);
                    if (!career.Byte23B8) career.SetOfferSeed(i, offer.Seed);
                    result.Add(offer);
                }
                return result;
            }
            // The story is over: three generated offers.
            int listed = 0, written = 0;
            var planets = new string[0x17];
            for (int i = 0; i < 0x17; i++) planets[i] = data.Tables.Economy.Planets[i];
            for (int i = 0; i < 0x17; i++)
                if (career.I32(0x23d3 + i * 4) == 0 && listed < 3)
                {
                    if (!career.Byte23B8)
                    {
                        var name = planets[i].ToCharArray();
                        if (name.Length >= 7) { name[4] = 'c'; name[5] = 'a'; name[6] = 'm'; }
                        career.SetStr(0x202 + written * 0x10, new string(name));
                        career.SetStr(0x2a2 + written * 0x10, new string(name));
                        written++;
                    }
                    listed++;
                }
            var seeds = new int[3];
            var generated = new bool[3];
            if (!career.Byte23B8)
                for (int i = 0; i < 3; i++)
                {
                    data.Random.Rand();                     // rand() % 9, unused
                    career.SetStr(0x202 + i * 0x10, "RAN1cam1");
                    career.SetStr(0x2a2 + i * 0x10, "RAN1cam1");
                    seeds[i] = data.Random.Rand();
                    generated[i] = true;
                    career.SetOfferSeed(i, i);
                }
            for (int i = 0; i < 3; i++)
            {
                var offer = new Offer { Generated = generated[i] };
                if (!generated[i]) Read(career, data, 0x202 + i * 0x10, offer, tickSeed, newSeed, tick, rmgError);
                else Read(career, data, 0x202 + i * 0x10, offer, (seeds[i] & 0xff) == 1, (seeds[i] & 0xff) == 1, tick, rmgError);
                result.Add(offer);
            }
            return result;
        }

        /// <summary>Contract_Read FUN_1006b2e3 for the career's list entry at <paramref name="at"/> (upper-cased in place by
        /// FUN_1000739d, as the original does). Returns its low byte: true for a step read from MW2.PRJ.</summary>
        static bool Read(Career career, ShellData data, int at, Offer offer, bool tickSeed, bool newSeed, int tick, Action<string> rmgError)
        {
            string name = career.Str(at, 0x10);
            return Read(career, data, name, at, offer, tickSeed, newSeed, tick, rmgError);
        }

        static bool Read(Career career, ShellData data, string name, int at, Offer offer, bool tickSeed, bool newSeed, int tick,
                         Action<string> rmgError)
        {
            offer.Step = offer.Step ?? name;
            bool loose = name.StartsWith("RND", StringComparison.Ordinal);
            byte[] bwd = loose ? data.ReadLoose(name) : data.ReadBwd(name);
            if (bwd == null)
            {
                offer.Text = $"No Contract on File for: {name}\\n\\n";
                return false;
            }
            bool ran = false;
            int number = 0;
            string text = "";
            int p = 0xc, end = BitConverter.ToInt32(bwd, 4);
            while (p + 8 <= end && p + 8 <= bwd.Length)
            {
                string tag = Encoding.ASCII.GetString(bwd, p, 4);
                int length = BitConverter.ToInt32(bwd, p + 4);
                if (tag == "CPGN")
                {
                    offer.Employer = Zero(bwd, p + 0x90, 0x10);
                    string upper = name.ToUpperInvariant();
                    if (at >= 0)
                    {
                        career.SetStr(at, upper);
                        name = upper;
                    }
                    if (upper.StartsWith("RAN", StringComparison.Ordinal))
                    {
                        ran = true;
                        number = Atoi(upper.Length > 3 ? upper.Substring(3, 1) : "");
                        offer.Seed = career.RunRmg(data, number, BitConverter.ToInt32(bwd, p + 0x64), BitConverter.ToInt32(bwd, p + 0x68),
                            tickSeed, newSeed, tick, out int failed);
                        string message = RandomMissionGenerator.ErrorText(failed, data.Tables);
                        if (message != null) rmgError?.Invoke(message);
                    }
                    p += 0xa0;
                    continue;
                }
                if (tag == "CTNT") text = Zero(bwd, p + 8, Math.Max(0, length - 8));
                if (length < 8) break;
                p += length;
            }
            if (ran) return Read(career, data, $"RND{number}{name.Substring(4)}", -1, offer, tickSeed, newSeed, tick, rmgError);
            offer.Text = text;
            return !loose;
        }

        static string Zero(byte[] d, int o, int max)
        {
            int e = o;
            while (e < o + max && e < d.Length && d[e] != 0) e++;
            return Encoding.ASCII.GetString(d, o, e - o);
        }

        static int Atoi(string s)
        {
            int v = 0;
            foreach (char ch in s)
            {
                if (ch < '0' || ch > '9') break;
                v = v * 10 + (ch - '0');
            }
            return v;
        }
    }
}
