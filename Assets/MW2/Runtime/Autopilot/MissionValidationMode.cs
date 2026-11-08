using System;
using System.Collections.Generic;
using UnityEngine;

namespace MW2.Autopilot
{
    /// <summary>Scenario observer, separate from fair results. Counts exercised transitions; never writes objective states.</summary>
    public sealed class MissionValidationMode
    {
        public readonly ValidationCoverage Coverage = new ValidationCoverage();
        readonly Mw2MissionDirector _director;
        readonly HashSet<int> _navs = new HashSet<int>();
        readonly Dictionary<(int, int), Mw2MissionDirector.State> _orders = new Dictionary<(int, int), Mw2MissionDirector.State>();
        readonly Dictionary<Mw2MechCombat, int> _units = new Dictionary<Mw2MechCombat, int>();
        public MissionValidationMode(Mw2MissionDirector director, bool invulnerabilityRequested)
        {
            _director = director; Coverage.invulnerabilityRequested = invulnerabilityRequested;
            Coverage.effectiveInvulnerability = Mw2Options.GameOptions.Invulnerable;
        }
        public void Sample()
        {
            for (int i = 0; i < _director.Navs.Count; i++)
                if (_director.NavVisited(i) && _navs.Add(i))
                { Coverage.navTriggers++; MissionTelemetry.Note("validation_nav", _director.Navs[i].File); }
            foreach (var team in _director.Teams)
                for (int i = 0; i < team.Orders.Count; i++)
                {
                    var key = (team.Team, i); var state = _director.StateOf(team.Team, i);
                    bool known = _orders.TryGetValue(key, out var old);
                    if (!known || old != state)
                    {
                        if (state == Mw2MissionDirector.State.Active) Coverage.objectiveActivations++;
                        if (state == Mw2MissionDirector.State.Succeeded) Coverage.objectiveCompletions++;
                    }
                    _orders[key] = state;
                }
            foreach (var unit in Mw2MechCombat.All)
            {
                if (unit == null) continue;
                int flags = unit.GamepieceFlags;
                if (!_units.TryGetValue(unit, out int previous)) Coverage.unitRegistrations++;
                else
                {
                    if ((previous & 0x800) != 0 && (flags & 0x800) == 0) Coverage.unitReveals++;
                    if ((previous & 6) == 0 && (flags & 6) != 0) Coverage.unitDeaths++;
                }
                _units[unit] = flags;
            }
            Coverage.originalFinishObserved = _director.MissionResult != Mw2MissionDirector.Result.Running;
        }
        public void Finish()
        {
            Sample();
            var notes = new List<string>();
            if (Coverage.navTriggers == 0) notes.Add("No NAV arrival observed; NAV-trigger coverage remains unverified");
            if (Coverage.unitReveals == 0) notes.Add("No hidden-to-visible unit transition observed; reinforcement coverage remains unverified");
            if (!Coverage.originalFinishObserved) notes.Add("No original mission finish observed before test deadline");
            notes.Add("Coverage counts are observations, not proof that every scenario branch is correct");
            Coverage.reviewNotes = notes.ToArray();
        }
    }
}
