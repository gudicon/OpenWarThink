namespace MW2.Formats
{
    /// <summary>FUN_100105c5(1): prune the radio linked list and latch new-message rejection.
    /// +0x0c is a resource pointer, not the display text at +0x1c. A removed head's
    /// successor becomes the new head and is exempt from the waiting-node filter.</summary>
    public struct RadioExit
    {
        public bool RemoveHead, BlockNewMessages;

        /// <summary>Head handling at 0x100105cb..0x10010630; an empty result returns before setting the latch.</summary>
        public static RadioExit Evaluate(bool hasHead, bool blocked, int firstSample, int priority, int waitingCount)
        {
            bool remove = hasHead && !blocked && firstSample > 0 && priority < 0x50;
            return new RadioExit { RemoveHead = remove,
                BlockNewMessages = blocked || (hasHead && (!remove || waitingCount > 0)) };
        }

        /// <summary>Waiting-node predicate at 0x10010652..0x10010673 for keepImportant=1.</summary>
        public static bool KeepWaiting(int firstSample, bool hasResource, int priority)
            => hasResource || (firstSample > 0 && priority >= 0x50);
    }
}
