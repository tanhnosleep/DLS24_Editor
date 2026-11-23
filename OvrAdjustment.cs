using System;

namespace Dls24Editor
{
    internal static class OvrAdjustment
    {
        private const int MaxStat = 1000;

        public static bool TryGetStats(int[] stats, int[] weightsInDbOrder, int targetOvr,
            Func<int[], int> calculateNativeRating, out int[] adjusted)
        {
            ArgumentNullException.ThrowIfNull(stats);
            ArgumentNullException.ThrowIfNull(weightsInDbOrder);
            ArgumentNullException.ThrowIfNull(calculateNativeRating);
            if (stats.Length != weightsInDbOrder.Length)
                throw new ArgumentException("Weights must be in the same order as the stats.", nameof(weightsInDbOrder));

            adjusted = (int[])stats.Clone();
            int CurrentOvr(int[] values) => calculateNativeRating(values) / 100;
            if (CurrentOvr(stats) == targetOvr)
                return true;

            // Keep attributes that do not affect this position's OVR, including their original values.
            int[] baseline = (int[])stats.Clone();
            for (int i = 0; i < baseline.Length; i++)
                if (weightsInDbOrder[i] != 0)
                    baseline[i] = Math.Clamp(baseline[i], 0, MaxStat);

            int direction = targetOvr.CompareTo(CurrentOvr(baseline));
            if (direction == 0)
            {
                adjusted = baseline;
                return true;
            }

            // Moving rated attributes by the same amount preserves their differences until a bound is reached.
            int[] WithShift(int shift)
            {
                var candidate = (int[])baseline.Clone();
                for (int i = 0; i < candidate.Length; i++)
                {
                    int attributeDirection = direction * Math.Sign(weightsInDbOrder[i]);
                    candidate[i] = attributeDirection == 0
                        ? baseline[i]
                        : Math.Clamp(baseline[i] + attributeDirection * shift, 0, MaxStat);
                }
                return candidate;
            }

            bool ReachesTarget(int[] values) => direction * CurrentOvr(values).CompareTo(targetOvr) >= 0;
            if (!ReachesTarget(WithShift(MaxStat)))
                return false;

            int low = 0;
            int high = MaxStat;
            while (high - low > 1)
            {
                int middle = (low + high) / 2;
                if (ReachesTarget(WithShift(middle)))
                    high = middle;
                else
                    low = middle;
            }

            var shifted = WithShift(high);
            if (CurrentOvr(shifted) == targetOvr)
            {
                adjusted = shifted;
                return true;
            }

            // A custom set of weights can cross two OVRs at once when every stat steps together.
            // Split that last step to find an exact result with a near-uniform change.
            var previous = WithShift(low);
            for (int i = 0; i < shifted.Length; i++)
            {
                if (previous[i] == shifted[i]) continue;
                previous[i] = shifted[i];
                if (CurrentOvr(previous) == targetOvr)
                {
                    adjusted = previous;
                    return true;
                }
            }
            return false;
        }
    }
}
