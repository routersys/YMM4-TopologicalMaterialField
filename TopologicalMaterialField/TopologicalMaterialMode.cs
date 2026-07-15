using System.ComponentModel.DataAnnotations;

namespace TopologicalMaterialField;

public enum TopologicalMaterialMode
{
    [Display(Name = nameof(Texts.MaterialCeramic), Description = nameof(Texts.MaterialCeramicDescription), ResourceType = typeof(Texts))]
    Ceramic = 0,

    [Display(Name = nameof(Texts.MaterialMineral), Description = nameof(Texts.MaterialMineralDescription), ResourceType = typeof(Texts))]
    Mineral = 1,

    [Display(Name = nameof(Texts.MaterialOxidizedMetal), Description = nameof(Texts.MaterialOxidizedMetalDescription), ResourceType = typeof(Texts))]
    OxidizedMetal = 2,

    [Display(Name = nameof(Texts.MaterialParchment), Description = nameof(Texts.MaterialParchmentDescription), ResourceType = typeof(Texts))]
    Parchment = 3,

    [Display(Name = nameof(Texts.MaterialIceCrystal), Description = nameof(Texts.MaterialIceCrystalDescription), ResourceType = typeof(Texts))]
    IceCrystal = 4,

    [Display(Name = nameof(Texts.MaterialTextile), Description = nameof(Texts.MaterialTextileDescription), ResourceType = typeof(Texts))]
    Textile = 5,
}
