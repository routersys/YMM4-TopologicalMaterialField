using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;

namespace TopologicalMaterialField;

[VideoEffect(nameof(Texts.TopologicalMaterialField), [VideoEffectCategories.Filtering, VideoEffectCategories.Decoration], [nameof(Texts.TagMaterial), nameof(Texts.TagTopology), nameof(Texts.TagStylize)], IsAviUtlSupported = false, ResourceType = typeof(Texts))]
public sealed class TopologicalMaterialFieldEffect : VideoEffectBase
{
    public override string Label => Texts.TopologicalMaterialField;

    public TopologicalMaterialFieldEffect()
    {
        TopologicalMaterialFieldUpdateNotifier.EnsureCheckedOnce();
    }

    [Display(GroupName = nameof(Texts.BasicGroup), Name = nameof(Texts.Amount), Description = nameof(Texts.AmountDescription), Order = 0, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "%", 0, 100)]
    public Animation Amount { get; } = new Animation(100, 0, 100);

    [Display(GroupName = nameof(Texts.BasicGroup), Name = nameof(Texts.Material), Description = nameof(Texts.MaterialDescription), Order = 1, ResourceType = typeof(Texts))]
    [EnumComboBox]
    public TopologicalMaterialMode Material { get => _material; set => Set(ref _material, value); }
    private TopologicalMaterialMode _material = TopologicalMaterialMode.Ceramic;

    [Display(GroupName = nameof(Texts.BasicGroup), Name = nameof(Texts.Quality), Description = nameof(Texts.QualityDescription), Order = 2, ResourceType = typeof(Texts))]
    [EnumComboBox]
    public TopologicalMaterialQuality Quality { get => _quality; set => Set(ref _quality, value); }
    private TopologicalMaterialQuality _quality = TopologicalMaterialQuality.High;

    [Display(GroupName = nameof(Texts.TopologyGroup), Name = nameof(Texts.TopologyScale), Description = nameof(Texts.TopologyScaleDescription), Order = 10, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "px", 1, 8)]
    public Animation TopologyScale { get; } = new Animation(3, 1, 8);

    [Display(GroupName = nameof(Texts.TopologyGroup), Name = nameof(Texts.FeatureThreshold), Description = nameof(Texts.FeatureThresholdDescription), Order = 11, ResourceType = typeof(Texts))]
    [AnimationSlider("F2", "%", 0, 10)]
    public Animation FeatureThreshold { get; } = new Animation(1, 0, 25);

    [Display(GroupName = nameof(Texts.DistributionGroup), Name = nameof(Texts.Distribution), Description = nameof(Texts.DistributionDescription), Order = 20, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "%", 0, 100)]
    public Animation Distribution { get; } = new Animation(85, 0, 100);

    [Display(GroupName = nameof(Texts.DistributionGroup), Name = nameof(Texts.ColorVariation), Description = nameof(Texts.ColorVariationDescription), Order = 21, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "%", 0, 200)]
    public Animation ColorVariation { get; } = new Animation(70, 0, 200);

    [Display(GroupName = nameof(Texts.PatternGroup), Name = nameof(Texts.PatternScale), Description = nameof(Texts.PatternScaleDescription), Order = 30, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "px", 1, 100)]
    public Animation PatternScale { get; } = new Animation(24, 1, 256);

    [Display(GroupName = nameof(Texts.PatternGroup), Name = nameof(Texts.PatternStrength), Description = nameof(Texts.PatternStrengthDescription), Order = 31, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "%", 0, 200)]
    public Animation PatternStrength { get; } = new Animation(65, 0, 200);

    [Display(GroupName = nameof(Texts.ReconstructionGroup), Name = nameof(Texts.Reconstruction), Description = nameof(Texts.ReconstructionDescription), Order = 40, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "%", 0, 200)]
    public Animation Reconstruction { get; } = new Animation(100, 0, 200);

    [Display(GroupName = nameof(Texts.ReconstructionGroup), Name = nameof(Texts.Relief), Description = nameof(Texts.ReliefDescription), Order = 41, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "%", 0, 200)]
    public Animation Relief { get; } = new Animation(75, 0, 200);

    [Display(GroupName = nameof(Texts.LightingGroup), Name = nameof(Texts.LightAngle), Description = nameof(Texts.LightAngleDescription), Order = 50, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "°", -180, 180)]
    public Animation LightAngle { get; } = new Animation(-35, -180, 180);

    [Display(GroupName = nameof(Texts.LightingGroup), Name = nameof(Texts.LightElevation), Description = nameof(Texts.LightElevationDescription), Order = 51, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "°", 1, 89)]
    public Animation LightElevation { get; } = new Animation(42, 1, 89);

    [Display(GroupName = nameof(Texts.PatternGroup), Name = nameof(Texts.Seed), Description = nameof(Texts.SeedDescription), Order = 32, ResourceType = typeof(Texts))]
    [Range(0, int.MaxValue)]
    [DefaultValue(0)]
    [TextBoxSlider("F0", "", 0, 10000)]
    public int Seed
    {
        get => _seed;
        set => Set(ref _seed, Math.Max(value, 0));
    }
    private int _seed;

    private IAnimatable[]? _animatables;

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription) => [];

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new TopologicalMaterialFieldEffectProcessor(devices, this);

    protected override IEnumerable<IAnimatable> GetAnimatables()
        => _animatables ??= [Amount, TopologyScale, FeatureThreshold, Distribution, ColorVariation, PatternScale, PatternStrength, Reconstruction, Relief, LightAngle, LightElevation];
}
