using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// A port extension that draws weapons in its own way, next to or instead of the original pictures. The ported weapon
    /// code only reports what happens (a weapon fired, a shot started, hit, went away) and asks whether to draw the original
    /// pictures; it never changes damage, heat, rate, range, speed, sound or hits. With no extension
    /// (<see cref="Mw2WeaponView.Current"/> null) everything is the original's.
    /// </summary>
    public interface IWeaponView
    {
        /// <summary>A weapon fired at <paramref name="muzzle"/> along <paramref name="direction"/>; returns whether the
        /// original muzzle effects are drawn.</summary>
        bool Fired(WeaponDef def, Mw2MechCombat owner, Vector3 muzzle, Vector3 direction);

        /// <summary>A shot of <paramref name="def"/> is about to start; <paramref name="reach"/> is the farthest it can fly
        /// (metres). Returns its picture, or null when the shot looks as in the original.</summary>
        IShotView Shot(WeaponDef def, Mw2MechCombat owner, Vector3 muzzle, Vector3 direction, float reach);
    }

    /// <summary>The picture of one flying shot drawn by a port extension (<see cref="IWeaponView.Shot"/>).</summary>
    public interface IShotView
    {
        /// <summary>Whether the shot's original model, point light and ground glow are drawn.</summary>
        bool DrawsOriginal { get; }

        /// <summary>The shot's object is placed at its start and faces its flight; it moves and turns with the shot.</summary>
        void Started(Transform shot);

        /// <summary>The shot hit at <paramref name="point"/>; <paramref name="back"/> points the way it came. Returns
        /// whether the original impact effect is drawn (its sound, area damage and debris happen either way).</summary>
        bool Hit(Vector3 point, Vector3 back);

        /// <summary>The shot is removed at <paramref name="point"/>: after a hit, or when it ran out.</summary>
        void Gone(Vector3 point);
    }

    /// <summary>The extension that draws weapons, if any (port addition with no counterpart in the original program).</summary>
    public static class Mw2WeaponView
    {
        public static IWeaponView Current;

        /// <summary><see cref="IWeaponView.Fired"/>, or true (draw the original) without an extension.</summary>
        public static bool Fired(WeaponDef def, Mw2MechCombat owner, Vector3 muzzle, Vector3 direction)
        {
            if (Current == null) return true;
            try { return Current.Fired(def, owner, muzzle, direction); }
            catch (System.Exception e) { Debug.LogException(e); return true; }
        }

        /// <summary><see cref="IWeaponView.Shot"/>, or null without an extension.</summary>
        public static IShotView Shot(WeaponDef def, Mw2MechCombat owner, Vector3 muzzle, Vector3 direction, float reach)
        {
            if (Current == null) return null;
            try { return Current.Shot(def, owner, muzzle, direction, reach); }
            catch (System.Exception e) { Debug.LogException(e); return null; }
        }
    }
}
