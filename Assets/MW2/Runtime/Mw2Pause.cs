using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Pause and in-game windows, as SimMain 0x100465ae runs a frame: the simulation step (shots, effects, movement, AI,
    /// textures) is skipped while PAUSE_GAME is set (DAT_100aea94) or an in-game window is open (DAT_100ad380); the
    /// clock (FUN_10015f3d 0x10015f3d), input, HUD and objectives still run. FUN_10047a1d 0x10047a1d then enters the
    /// pause mode (DAT_100aea90): sample 0xd7 (FUN_10049915), PauseTimer(0x80, 1) stops the mission clock,
    /// sounds and music pause (FUN_1000fd4b) and the input updates only menu_ channels (FUN_1002ca3c); Input_HandleCommands
    /// is skipped and the next key code ends the pause with sample 0x15d (FUN_10049931). FUN_10049880 draws the HUD
    /// sprite 0x43 (PAUSED) centred while paused.
    /// Here the skipped step is Time.timeScale = 0 (every unit clock runs on scaled time); the mission clock and the
    /// input clock run on unscaled time and stop only in the pause mode.
    /// The main menu (window 4, Mw2Cockpit main menu) also holds the pause mode while it is open, and a PAUSE_GAME given
    /// meanwhile is dropped.
    /// </summary>
    public static class Mw2Pause
    {
        /// <summary>DAT_100aea94: PAUSE_GAME (command 0x70) was given.</summary>
        public static bool Paused { get; private set; }
        /// <summary>DAT_100aea90: the pause mode is on (timer, sound and commands stopped).</summary>
        public static bool PauseMode { get; private set; }

        /// <summary>Input_Command 0x100267c0, case 0x70.</summary>
        public static void RequestPause() => Paused = true;

        /// <summary>FUN_1003d2db(4): the main menu window is open.</summary>
        public static bool MainMenuOpen;

        /// <summary>Host lifecycle adapter: SimMain has returned, so no cockpit window may hold the next screen.</summary>
        public static void ResetForMissionExit()
        {
            Paused = PauseMode = MainMenuOpen = false;
            Mw2Input.MenuMode = Mw2Input.WindowOpen = Mw2Input.WindowTakesArrows = false;
            Time.timeScale = 1f;
        }

        /// <summary>FUN_10047a1d 0x10047a1d with a key code of this frame; returns true when the code was consumed.</summary>
        public static bool Update(bool keyCode)
        {
            bool consumed = false;
            var audio = Mw2Audio.Instance;
            if (Paused)
            {
                if (PauseMode && keyCode)
                {
                    // FUN_10049931: sample 0x15d on the voice channel (FUN_10007243), then the pause ends.
                    Paused = false;
                    consumed = true;
                    if (audio != null) audio.ResumeFromPause();
                    if (audio != null && Camera.main != null) audio.Play(0x15d, Camera.main.transform.position, true);
                }
                else if (MainMenuOpen) Paused = false;
            }
            if (!Paused && !MainMenuOpen)
            {
                if (PauseMode)
                {
                    PauseMode = false;
                    if (audio != null) audio.ResumeFromPause();
                    Mw2Input.MenuMode = false;
                }
            }
            else if (!PauseMode)
            {
                // FUN_10049915 starts sample 0xd7 on the voice channel just before FUN_1000fd4b stops that channel, so
                // nothing of it is heard; it is not played here.
                PauseMode = true;
                if (audio != null) audio.PauseForGame();
                Mw2Input.MenuMode = true;
            }
            Time.timeScale = Paused || Mw2Input.WindowOpen ? 0f : 1f;
            return consumed;
        }
    }
}
