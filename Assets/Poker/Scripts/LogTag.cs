public static class LogTag
{
    // Tier 1 — Critical Systems (max verbosity)
    public const string Game       = "Game";
    public const string Network    = "Network";
    public const string Save       = "Save";
    public const string Auth       = "Auth";
    public const string Migration  = "Migration";
    public const string Exit       = "Exit";

    // Tier 2 — Gameplay Systems (moderate verbosity)
    public const string Table      = "Table";
    public const string Player     = "Player";
    public const string BuyIn      = "BuyIn";
    public const string Hand       = "Hand";
    public const string History    = "History";
    public const string Chat       = "Chat";
    public const string Cards      = "Cards";
    public const string Stats      = "Stats";
    public const string Version    = "Version";

    // Tier 3 — Infrastructure Systems (minimal verbosity)
    public const string UI         = "UI";
    public const string Audio      = "Audio";
    public const string Theme      = "Theme";
}
