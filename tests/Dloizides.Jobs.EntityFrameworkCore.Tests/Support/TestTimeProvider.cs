namespace Dloizides.Jobs.EntityFrameworkCore.Tests.Support;

/// <summary>
/// A controllable clock: <see cref="GetUtcNow"/> returns a settable instant so a test can EXPIRE a lease or
/// age a job deterministically without sleeping. Timer creation is inherited from the base (a real system
/// timer), which the tests never rely on — they drive the runner and watchdog synchronously.
/// </summary>
public sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public TestTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Move the clock forward by <paramref name="delta"/>.</summary>
    public void Advance(TimeSpan delta) => _now += delta;

    /// <summary>Set the clock to an absolute instant.</summary>
    public void Set(DateTimeOffset now) => _now = now;
}
