using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Plays a mech's ANIM through the ported state machine (AnimTask). The movement code drives it through
    /// <see cref="TargetState"/> (gait selection, mercsw.dll 0x10028abb): -1 stop, 0 walk, 1 run, 2 reverse.
    /// </summary>
    public class Mw2MechAnimator : MonoBehaviour
    {
        [Tooltip("Requested gait state (+0x88): -1 stop, 0 walk, 1 run, 2 reverse.")]
        public int TargetState = -1;
        [Tooltip("1 slow (1.5x frame time), 2 normal, 3 fast (0.75x).")]
        [Range(0, 3)] public int Gait = 2;
        public float TimeScale = 1f;
        [Tooltip("SNDS id of the mech's ASND step sound (MECHVFT1 / MECFSMED / MECLIGHT); -1 none, 0 not read (rebuild).")]
        public int StepSound;
        [Tooltip("Set by Mw2MechController: the clock is advanced through Tick() instead of Update().")]
        public bool ExternalClock;

        [SerializeField, HideInInspector] byte[] _animData;
        [SerializeField, HideInInspector] string[] _taskSpecs;
        [SerializeField, HideInInspector] int[] _nodeIds;
        [SerializeField, HideInInspector] Transform[] _nodeTransforms;
        [SerializeField] string _animName;

        readonly MechAnimState _state = new MechAnimState();
        readonly List<AnimTask> _tasks = new List<AnimTask>();
        double _clock;

        public int CurrentState => _state.CurrentState;
        public bool FootContact => (_state.Flags & MechAnimState.FlagFootContact) != 0;
        public bool Footstep => (_state.Flags & MechAnimState.FlagFootstep) != 0;
        public int RequestedState => _state.TargetState;

        /// <summary>Mech_SelectGait 0x10028abb takes the footstep flag 8 (set by frame flag 0x800) and clears it.</summary>
        public bool TakeFootstep()
        {
            if ((_state.Flags & MechAnimState.FlagFootstep) == 0) return false;
            _state.Flags &= ~MechAnimState.FlagFootstep;
            return true;
        }

        public void Setup(string animName, byte[] animData, IList<string> taskSpecs, IDictionary<int, Transform> nodes)
        {
            _animName = animName;
            _animData = animData;
            _taskSpecs = new string[taskSpecs.Count];
            taskSpecs.CopyTo(_taskSpecs, 0);
            _nodeIds = new int[nodes.Count];
            _nodeTransforms = new Transform[nodes.Count];
            int i = 0;
            foreach (var kv in nodes)
            {
                _nodeIds[i] = kv.Key;
                _nodeTransforms[i] = kv.Value;
                i++;
            }
            // AddComponent calls Awake before Setup in a mission built at runtime.
            // Initialise here as well as in Awake (which handles serialized editor scenes).
            InitializeTasks();
        }

        void Awake()
        {
            InitializeTasks();
        }

        void InitializeTasks()
        {
            if (_animData == null || _animData.Length == 0) return;
            _tasks.Clear();
            var anim = Anim.Decode(_animData);
            var nodes = new Dictionary<int, Transform>();
            for (int i = 0; i < _nodeIds.Length; i++) nodes[_nodeIds[i]] = _nodeTransforms[i];

            foreach (var spec in _taskSpecs)
            {
                if (!AnimTask.TryParse(spec, out int node, out int period, out int flags, out int track)) continue;
                if (track >= anim.Tracks.Length || !nodes.TryGetValue(node, out var target)) continue;
                var task = new AnimTask(anim, _state, node, period, flags, track, 0);
                task.Apply = (type, amount) => ApplyDelta(target, type, amount);
                _tasks.Add(task);
            }
        }

        /// <summary>
        /// Incremental move in the node's local frame; MW2 and Unity axes agree (PolyMeshBuilder.ToUnity).
        /// </summary>
        static void ApplyDelta(Transform t, int type, float amount)
        {
            switch (type)
            {
                case 0: t.localPosition += new Vector3(amount * PolyModel.UnitsToMeters, 0, 0); break;
                case 1: t.localPosition += new Vector3(0, amount * PolyModel.UnitsToMeters, 0); break;
                case 2: t.localPosition += new Vector3(0, 0, amount * PolyModel.UnitsToMeters); break;
                case 3: t.localRotation *= Quaternion.AngleAxis(amount, Vector3.right); break;
                case 4: t.localRotation *= Quaternion.AngleAxis(amount, Vector3.up); break;
                case 5: t.localRotation *= Quaternion.AngleAxis(amount, Vector3.forward); break;
            }
        }

        void Update()
        {
            if (ExternalClock) return;
            _clock += Time.deltaTime * TimeScale * MechMotion.TicksPerSecond;
            Tick((int)_clock);
        }

        /// <summary>Advances all animation tasks to game clock <paramref name="now"/> (ticks).</summary>
        public void Tick(int now)
        {
            if (_tasks.Count == 0) return;
            _state.TargetState = TargetState;
            _state.Gait = Gait;
            foreach (var task in _tasks) task.Update(now);
        }
    }
}
