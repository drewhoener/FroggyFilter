using System.Collections.Generic;

namespace FroggyFilter.Framework;

public class ModConfig
{
    public bool ExcludeEradicationGoals { get; set; } = false;
    public Dictionary<string, bool> EnabledMonsters { get; set; } = new();
    public string ExcludedMonsters { get; set; } = string.Empty;
}