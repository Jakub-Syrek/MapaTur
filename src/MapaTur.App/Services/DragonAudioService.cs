namespace MapaTur.App.Services;

/// <summary>
/// Procedural dragon sound effects — a roaring fire-breath loop, wing-beat whooshes, fireball explosions
/// and lake-quench steam. Everything is SYNTHESIZED on first use (no audio assets to ship) and played
/// through the platform media player. Desktop-only, like the dragons themselves: the Windows partial
/// (Platforms/Windows) carries the whole implementation; on other targets the partial methods have no
/// body, so the compiler elides these calls entirely.
/// </summary>
public sealed partial class DragonAudioService
{
    /// <summary>Starts/stops the looped breath roar. Idempotent per state — safe to call every tick.</summary>
    public void SetFireActive(bool active) => SetFireActiveImpl(active);

    /// <summary>One-shot impact blast; power scales it (stream 1.1 → kill 2.2), distance attenuates it.</summary>
    public void PlayExplosion(float power, float distanceMeters) => PlayExplosionImpl(power, distanceMeters);

    /// <summary>One-shot steam hiss for a fireball quenched on a lake.</summary>
    public void PlaySteam(float distanceMeters) => PlaySteamImpl(distanceMeters);

    /// <summary>One wing-beat whoosh; vigor (the flap activity, 0..~1.5) drives the volume.</summary>
    public void PlayFlap(float vigor) => PlayFlapImpl(vigor);

    /// <summary>A deep dragon roar (entry cry, kill scream, occasional soar call). Volume 0..1.</summary>
    public void PlayRoar(float volume) => PlayRoarImpl(volume);

    /// <summary>
    /// Continuous flight bed — three looping layers whose levels (0..1) ride the flight state each tick:
    /// speed wind, wing-membrane flutter, and the ground-proximity rush of a low pass. All zeros pauses the
    /// loops. Levels are smoothed inside the implementation, so callers can pass raw values every tick.
    /// </summary>
    public void SetFlightBed(float windLevel, float wingLevel, float groundLevel)
        => SetFlightBedImpl(windLevel, wingLevel, groundLevel);

    partial void SetFireActiveImpl(bool active);

    partial void PlayExplosionImpl(float power, float distanceMeters);

    partial void PlaySteamImpl(float distanceMeters);

    partial void PlayFlapImpl(float vigor);

    partial void PlayRoarImpl(float volume);

    partial void SetFlightBedImpl(float windLevel, float wingLevel, float groundLevel);
}