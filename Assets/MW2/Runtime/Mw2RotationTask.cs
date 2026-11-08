using UnityEngine;

namespace MW2
{
    /// <summary>
    /// TSK class 0, FUN_1000e170 0x1000e170: each game tick rotates its node by
    /// axis angle * elapsedTicks / (int)(periodSeconds * 181). The five valid tasks in
    /// IVA1ARE1 rotate only about model Z; FUN_1005a926 right-multiplies the node matrix.
    /// </summary>
    public sealed class Mw2RotationTask : MonoBehaviour
    {
        public float ZDegrees;
        public int PeriodTicks = 181;
        float _fractionalTicks;

        void Update()
        {
            if (PeriodTicks == 0) return;
            _fractionalTicks += Time.deltaTime * 181f;
            int ticks = Mathf.FloorToInt(_fractionalTicks);
            if (ticks <= 0) return;
            _fractionalTicks -= ticks;
            transform.localRotation *= Quaternion.AngleAxis(ZDegrees * ticks / PeriodTicks, Vector3.forward);
        }
    }
}
