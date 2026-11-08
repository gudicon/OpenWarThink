namespace MW2.Formats
{
    /// <summary>Global-list order from 0x10049159 / 0x100492b6 and strict first-hit
    /// selection 0x10018ad5. Unity surfaces and native part spheres share one activation sequence.
    /// Initial Unity node traversal and unmarked legacy colliders remain scene adapters.</summary>
    public static class SceneCollisionOrder
    {
        static long _sequence;
        /// <summary>0x10049159 inserts a newly enabled model before all existing models.</summary>
        public static long Activate() => ++_sequence;

        /// <summary>0x10018c60 keeps the first equal clipped length in list order.
        /// Compare stamps to merge Unity ray results with separately tested native spheres.</summary>
        public static bool Precedes(float distance, long order, float best, long bestOrder) =>
            distance < best || distance == best && order > bestOrder;
    }
}
