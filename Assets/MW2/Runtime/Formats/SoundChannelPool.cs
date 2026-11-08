namespace MW2.Formats
{
    /// <summary>
    /// Channel scan of Sound_Alloc 0x10006839: first free channel wins; when all are occupied,
    /// the last channel with strictly lower priority is replaced. FirstAudio 0x1000fbd6 requests eight.
    /// The caller releases finished channels and stops the previous source before using a replacement.
    /// </summary>
    public sealed class SoundChannelPool
    {
        readonly int[] _ids;
        readonly int[] _priorities;
        readonly bool[] _protected;

        public SoundChannelPool(int count)
        {
            _ids = new int[count];
            _priorities = new int[count];
            _protected = new bool[count];
        }

        public int Count => _ids.Length;
        public int SoundAt(int channel) => _ids[channel];

        public void Release(int channel)
        {
            _ids[channel] = 0;
            _priorities[channel] = 0;
            _protected[channel] = false;
        }

        // Sound_OnEnd 0x10006e09 starts the second radio sample in its reserved channel.
        public void ReplaceInChannel(int channel, int id) => _ids[channel] = id;

        /// <returns>Channel index, or -1 when no channel can be taken.</returns>
        public int Allocate(int id, int priority, int copies, bool protect = false)
        {
            if (copies > 0)
            {
                int playing = 0;
                for (int i = 0; i < Count; i++) if (_ids[i] == id) playing++;
                if (playing >= copies) return -1;
            }
            int victim = -1;
            for (int i = 0; i < Count; i++)
            {
                if (_ids[i] == 0) { victim = i; break; }
                if (!_protected[i] && _priorities[i] < priority) victim = i;
            }
            if (victim >= 0)
            {
                _ids[victim] = id;
                _priorities[victim] = priority;
                _protected[victim] = protect;
            }
            return victim;
        }
    }
}
