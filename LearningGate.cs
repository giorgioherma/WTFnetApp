using System;

namespace WTFnetApp
{
    public enum GamePhase
    {
        Connecting,
        Loading,
        Active,
        Idle
    }

    public class LearningGate
    {
        private DateTime disabledUntil = DateTime.MinValue;
        public bool LearningEnabled { get; private set; }
        public GamePhase CurrentPhase { get; private set; } = GamePhase.Active;

        public bool Update(MetricsSnapshot snapshot, double baselineRtt, GamePhase phase)
        {
            CurrentPhase = phase;

            if (snapshot != null && baselineRtt > 0 &&
                snapshot.ExternalPingMs > baselineRtt + 250 &&
                snapshot.QueueGrowth < 0.2)
            {
                disabledUntil = DateTime.UtcNow.AddSeconds(5);
            }

            LearningEnabled = phase == GamePhase.Active && DateTime.UtcNow >= disabledUntil;
            return LearningEnabled;
        }
    }
}
