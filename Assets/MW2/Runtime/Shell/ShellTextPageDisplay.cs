namespace MW2.Shell
{
    /// <summary>The shown side of a text page (textpage.cpp): its text objects and the typing sample.</summary>
    public sealed partial class ShellTextPage
    {
        /// <summary>+0x30: the typing sample; the screen supplies it (record 0x32, looping).</summary>
        public ShellSound TypeSound;

        /// <summary>Make the text objects (FUN_10076d16): normal lines in the page's font and remap, \l lines in the large
        /// font with <paramref name="largeRemap"/>.</summary>
        public void Create(ShellSurface surface, byte[] remap, byte[] largeRemap)
        {
            foreach (var l in Lines)
                if (l.Object == null)
                    l.Object = new ShellText(surface, l.Large ? _large : _font, l.Text, l.X, l.Y, l.Large ? largeRemap : remap);
        }

        /// <summary>FUN_10076e20 0x10076e20: draw every line as it stands.</summary>
        public void Draw()
        {
            foreach (var l in Lines) l.Object?.Draw();
        }

        /// <summary>FUN_10076e86 0x10076e86: start typing: the typing sample loops (volume 10) unless it already plays, and
        /// every line goes back to its start in typewriter mode.</summary>
        public void StartTyping()
        {
            if (TypeSound != null && !TypeSound.Playing)
            {
                TypeSound.SetVolume(10);
                TypeSound.Play();
            }
            foreach (var l in Lines) l.Object?.ResetTyping();
        }

        /// <summary>FUN_10076f3e 0x10076f3e: type one step of the first unfinished line; true (and the sample stops) when all
        /// lines are complete.</summary>
        public bool Advance()
        {
            foreach (var l in Lines)
            {
                if (l.Object == null || l.Object.Done) continue;
                l.Object.Advance();
                return false;
            }
            TypeSound?.Stop();
            return true;
        }

        /// <summary>FUN_10076fb6 0x10076fb6: stop the sample and take every line off the screen.</summary>
        public void Hide()
        {
            TypeSound?.Stop();
            foreach (var l in Lines) l.Object?.Hide();
        }
    }
}
