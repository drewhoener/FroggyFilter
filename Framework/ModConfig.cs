using System.Collections.Generic;

namespace FroggyFilter.Framework;

public class ModConfig
{
    public Dictionary<string, bool> EnabledMonsters { get; set; } = new();
    public string ExcludedMonsters { get; set; } = string.Empty;
}