using System;

namespace Cxt1024Bridge
{
    // Sol Low..Max, then Astra Light..Max. Ultra is press-only; the first
    // detent in either direction leaves Ultra through Astra Max.
    internal sealed class ReasoningAdjustment
    {
        internal const int AstraOffset = 5;
        internal const int UltraState = 10;
        internal const int SolUltraState = 11;
        private readonly int[] _targets = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

        internal void Add(bool increase)
        {
            for (int i = 0; i < _targets.Length; i++)
                _targets[i] = Resolve(_targets[i], increase);
        }

        internal static int Resolve(int current, bool increase)
        {
            if (current < 0 || current > SolUltraState) return -1;
            if (current == UltraState) return 9;
            if (current == SolUltraState) return 4;
            return Math.Max(0, Math.Min(9, current + (increase ? 1 : -1)));
        }

        internal int Target(bool astra, int rank)
        {
            if (rank < 0 || rank > 5) return -1;
            if (!astra && rank == 5) return _targets[SolUltraState];
            int current = (astra ? AstraOffset : 0) + rank;
            return _targets[current];
        }
    }
}
