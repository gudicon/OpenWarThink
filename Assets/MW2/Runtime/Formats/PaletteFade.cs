using System;

namespace MW2.Formats
{
    /// <summary>
    /// The palette fades of the engine (mercsw.dll): Palette_Fade 0x1006b46f, its step FUN_1004a653 0x1004a653 (state built
    /// by FUN_1004a545 0x1004a545), the per-frame FUN_1006b3f0 0x1006b3f0 + FUN_1006b34b 0x1006b34b (end of every frame,
    /// FUN_1001c1c3 0x1001c1c3), StartPalettes 0x1006b802 and FUN_1006b7bb 0x1006b7bb / FUN_1006b7ec 0x1006b7ec. The palettes
    /// are the 256 × 3 six-bit values of the mission's PALG entries (mode = index, 0..3 day, 4..7 dusk, 8..11 night, 12..15
    /// infrared, 16 black, 17 red, 18 and 19 white). A fade from the palette on the screen to a mode's palette runs in
    /// <c>steps</c> frames: each component moves by trunc(difference · k / steps) (FUN_1004a653 carries the remainder of
    /// acc / steps), so the last step lands exactly on the target. The steps are ticks / ticks-per-frame (DAT_100a6644; 20
    /// without a frame time, at least 1). Flag 0: a plain fade (from the running fade's palette while one runs, else from
    /// the palette of the committed mode DAT_100a1108); flag 1: out and back (ticks halved, the same number of steps back
    /// but the step count taken as ticks again, as DAT_100b1948 is passed to Palette_Fade); flag 2: one frame towards the
    /// target, then the back fade starts from the full target palette. A flag 1 or 2 request while a fade runs does nothing.
    /// </summary>
    public sealed class PaletteFade
    {
        public const int Count = 256;

        /// <summary>PAL data of a mode (768 six-bit values) or null: the table DAT_100b6740 of the PALG ids.</summary>
        public Func<int, byte[]> Palettes;

        /// <summary>DAT_100a1108: the committed mode (the palette the screen is at when no fade runs).</summary>
        public int Mode = 0x10;
        /// <summary>DAT_100a1110: the mode being faded to; DAT_100a1114: the mode the fade started from (-1: running fade).</summary>
        public int Pending, FromMode = -1;
        /// <summary>DAT_100b1944: the mode group set by the last period / infrared fade (flash offsets are added to it).</summary>
        public int Group;
        /// <summary>DAT_100a110c: the mode a finished fade settles on (FUN_1006b34b).</summary>
        public int Target = 0x10;
        /// <summary>DAT_100b194c: frames left; DAT_100b1948: the steps of the way back (0: none); DAT_100b1940: finished.</summary>
        public int Remaining, BackSteps;
        public bool Done;
        /// <summary>DAT_100a6644: ticks of the frame (0: unknown, fades take 20 steps).</summary>
        public int TicksPerFrame = 6;

        /// <summary>The palette on the screen (the six-bit values the display was last given).</summary>
        public readonly byte[] Display = new byte[Count * 3];
        /// <summary>Raised when Display changed.</summary>
        public bool Changed;

        // The running fade (the block DAT_100b1910): current values, per-step difference, carried remainder, steps, step.
        byte[] _cur;
        sbyte[] _delta;
        short[] _acc;
        int _steps, _step;

        /// <summary>A fade is running (a block is held), even when its frame counter is spent.</summary>
        public bool Running => _cur != null;

        /// <summary>StartPalettes 0x1006b802 (the first call of a mission): the palette of mode 16 on the screen.</summary>
        public void Begin()
        {
            var black = Palettes?.Invoke(0x10);
            if (black != null) Show(black);
            Mode = 0x10;
            Target = 0x10;
            _cur = null;
            Remaining = 0;
            Done = false;
        }

        void Show(byte[] palette)
        {
            Buffer.BlockCopy(palette, 0, Display, 0, Count * 3);
            Changed = true;
        }

        /// <summary>FUN_1006b388 0x1006b388: the palette of a mode shown as it is.</summary>
        public void Set(int mode)
        {
            var palette = Palettes?.Invoke(mode);
            if (palette != null) Show(palette);
        }

        /// <summary>FUN_1006b7bb 0x1006b7bb: a fade to a mode (ticks), which also becomes the group and the settle target.</summary>
        public void FadeGroup(int mode, int ticks)
        {
            Fade(mode, ticks, 0);
            Group = mode;
            Target = mode;
        }

        /// <summary>FUN_1006b64f 0x1006b64f: a fade to the mode group plus an offset.</summary>
        public void FadeOffset(int offset, int ticks, int flag) => Fade(Group + offset, ticks, flag);

        /// <summary>Palette_Fade 0x1006b46f. True when a fade was started.</summary>
        public bool Fade(int mode, int ticks, int flag)
        {
            byte[] from = null;
            int fromMode = -1;
            if (Remaining < 1 || flag != 0)
            {
                if (Remaining < 1)
                {
                    fromMode = Mode;
                    var p = Palettes?.Invoke(Mode);
                    if (p != null) from = (byte[])p.Clone();
                }
            }
            else from = _cur;
            if (from == null) return false;
            var to = Palettes?.Invoke(mode);
            if (to == null) return false;
            Pending = mode;
            FromMode = fromMode;
            int steps;
            if (TicksPerFrame == 0) steps = 0x14;
            else
            {
                if (flag == 1) ticks >>= 1;
                int fixedSteps = TicksPerFrame < 1 ? 10 : (int)(((long)ticks << 16) / TicksPerFrame);   // Fixed_Div16
                steps = fixedSteps < 0x10000 ? 1 : fixedSteps >> 16;
            }
            Remaining = flag == 2 ? 1 : steps;
            BackSteps = flag == 1 || flag == 2 ? steps : 0;
            // FUN_1004a545: current = from, per-step difference = to - from (signed bytes), remainders zero.
            var cur = new byte[Count * 3];
            var delta = new sbyte[Count * 3];
            Buffer.BlockCopy(from, 0, cur, 0, Count * 3);
            for (int i = 0; i < Count * 3; i++) delta[i] = unchecked((sbyte)(to[i] - from[i]));
            _cur = cur;
            _delta = delta;
            _acc = new short[Count * 3];
            _steps = steps;
            _step = 0;
            return true;
        }

        /// <summary>FUN_1004a653 0x1004a653: one step of the running fade; the block is let go after the last one.</summary>
        void StepBlock()
        {
            if (_cur == null || _step >= _steps) return;
            for (int i = 0; i < Count * 3; i++)
            {
                int acc = _acc[i] + _delta[i];
                _cur[i] = unchecked((byte)(_cur[i] + acc / _steps));
                _acc[i] = (short)(acc % _steps);
            }
            Show(_cur);
            _step++;
            if (_step >= _steps)
            {
                _cur = null;
                _delta = null;
                _acc = null;
            }
        }

        /// <summary>FUN_1006b3f0 0x1006b3f0 and FUN_1006b34b 0x1006b34b: what the end of every frame does.</summary>
        public void Frame()
        {
            if (Remaining != 0)
            {
                StepBlock();
                Remaining--;
                if (Remaining == 0)
                {
                    Mode = Pending;
                    if (BackSteps == 0) Done = true;
                    else Fade(FromMode, BackSteps, 0);
                }
            }
            if (Done && Remaining < 1)
            {
                Done = false;
                Set(Target);
                Mode = Target;
            }
        }
    }
}
