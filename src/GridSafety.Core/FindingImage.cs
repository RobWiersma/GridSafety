using System.Globalization;
using System.Net;
using System.Text;

namespace GridSafety.Core;

/// <summary>
/// Draws a synthetic "drone capture" of a finding as SVG: a pole with crossarm, insulators and conductors,
/// the defect drawn in, the detector's bounding box and a camera overlay. It stands in for the photo a real
/// drone would upload; nothing is stored, and the same finding always gets the same picture because the
/// scene is seeded from its id. Every image is badged SIMULATED so it can't be mistaken for a real one.
/// </summary>
public static class FindingImage
{
    public const int Width = 640;
    public const int Height = 400;

    public static string SeverityColor(int severity) => severity switch
    {
        >= 5 => "#dc2626",
        4 => "#ea580c",
        3 => "#ca8a04",
        2 => "#2563eb",
        _ => "#64748b",
    };

    public static string Svg(Finding f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var rng = new Random(StableSeed(f.Id));
        var thermal = f.Type == FindingType.ThermalHotspot;
        var s = new Scene(rng, thermal);

        var sb = new StringBuilder();
        sb.Append(Inv($"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {Width} {Height}" width="{Width}" height="{Height}" role="img" aria-label="{E(Label(f.Type))} at {E(f.StructureLabel)} (simulated)">"""));
        sb.Append(s.Background());

        // "Zoom" the camera onto the structure: draw it in scene coordinates, then scale about the crossarm.
        var cx = 290 + rng.Next(0, 60);
        const double cy = 175;
        sb.Append(Inv($"""<g transform="translate({cx} {cy}) scale({Zoom}) translate({-s.PoleX:0} {-s.ArmY:0})">"""));
        Box box = f.Type switch
        {
            FindingType.VegetationEncroachment => s.Vegetation(sb, f.Severity),
            FindingType.DamagedInsulator => s.CrackedInsulator(sb),
            FindingType.CrossarmDamage => s.LeaningCrossarm(sb, f.Severity),
            FindingType.ThermalHotspot => s.Hotspot(sb, f.Severity),
            FindingType.MissingHardware => s.MissingInsulator(sb),
            FindingType.ConductorDamage => s.FrayedConductor(sb),
            _ => s.Normal(sb),
        };
        sb.Append("</g>");
        box = new Box(cx + (box.X - s.PoleX) * Zoom, cy + (box.Y - s.ArmY) * Zoom, box.W * Zoom, box.H * Zoom);
        sb.Append(Detection(Clamp(box), f));
        if (thermal) sb.Append(ThermalScale);
        sb.Append(Overlay(f, thermal));
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// The same capture over real aerial imagery: a top-down (nadir) frame of the structure's actual location from
    /// <see cref="AerialFrame"/>, with the detection box on the structure, the camera HUD, a scale bar, the imagery
    /// credit and the SIMULATED badge (the photo is real; the defect is not).
    /// </summary>
    public static string AerialSvg(Finding f, byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(jpeg);
        var rng = new Random(StableSeed(f.Id));
        var thermal = f.Type == FindingType.ThermalHotspot;

        // The frame is centred on the structure. Box what the detector would: the pole and crossarm footprint,
        // or the canopy around it for vegetation. A little jitter, as real detections never sit dead centre.
        var (wm, hm) = f.Type == FindingType.VegetationEncroachment ? (26 + f.Severity * 2.0, 18 + f.Severity * 1.5) : (13.0, 11.0);
        var w = wm * AerialFrame.PixelsPerMeter;
        var h = hm * AerialFrame.PixelsPerMeter;
        var box = Clamp(new Box(Width / 2.0 - w / 2 + rng.Next(-6, 7), Height / 2.0 - h / 2 + rng.Next(-6, 7), w, h));

        var sb = new StringBuilder();
        sb.Append(Inv($"""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 {Width} {Height}" width="{Width}" height="{Height}" role="img" aria-label="{E(Label(f.Type))} at {E(f.StructureLabel)}, aerial view (simulated finding)">"""));
        if (thermal)
        {
            // False-colour "IR" look: luminance mapped onto the thermal palette, plus the hot spot on the structure.
            sb.Append("""
                <defs><filter id="ir" color-interpolation-filters="sRGB">
                <feColorMatrix type="matrix" values=".33 .33 .33 0 0 .33 .33 .33 0 0 .33 .33 .33 0 0 0 0 0 1 0"/>
                <feComponentTransfer><feFuncR type="table" tableValues=".12 .45 .75 .98"/><feFuncG type="table" tableValues=".06 .1 .3 .6"/><feFuncB type="table" tableValues=".2 .55 .5 .2"/></feComponentTransfer>
                </filter><radialGradient id="hot"><stop offset="0" stop-color="#fff"/><stop offset=".35" stop-color="#fde047"/><stop offset=".7" stop-color="#f97316" stop-opacity=".7"/><stop offset="1" stop-color="#f97316" stop-opacity="0"/></radialGradient></defs>
                """);
        }
        var data = Convert.ToBase64String(jpeg);
        sb.Append(Inv($"""<image width="{Width}" height="{Height}" preserveAspectRatio="xMidYMid slice" href="data:image/jpeg;base64,{data}" xlink:href="data:image/jpeg;base64,{data}"{(thermal ? " filter=\"url(#ir)\"" : "")}/>"""));
        if (thermal)
        {
            var r = 10 + f.Severity * 3;
            sb.Append(Inv($"""<circle cx="{box.X + box.W / 2:0}" cy="{box.Y + box.H / 2:0}" r="{r}" fill="url(#hot)"/>"""));
            sb.Append(ThermalScale);
        }
        else
        {
            // Slight drone-camera grade: lift the haze NAIP mosaics have and darken the corners.
            sb.Append("""<defs><radialGradient id="vig"><stop offset=".6" stop-color="#000" stop-opacity="0"/><stop offset="1" stop-color="#000" stop-opacity=".35"/></radialGradient></defs>""");
            sb.Append(Inv($"""<rect width="{Width}" height="{Height}" fill="url(#vig)"/>"""));
        }
        sb.Append(Detection(box, f));
        sb.Append(ScaleBar());
        sb.Append(Overlay(f, thermal, nadir: true));
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string ScaleBar()
    {
        const double meters = 20;
        var px = meters * AerialFrame.PixelsPerMeter;
        return Inv($"""
            <g font-family="ui-monospace,Consolas,monospace" font-size="11" fill="#fff">
            <rect x="16" y="{Height - 52}" width="{px:0}" height="5" fill="#fff" stroke="#000" stroke-opacity=".6"/>
            <text x="{16 + px + 6:0}" y="{Height - 46}" stroke="#000" stroke-width="3" stroke-opacity=".5" paint-order="stroke">{meters:0} m</text>
            <text x="{Width - 12}" y="{Height - 38}" text-anchor="end" font-size="10" stroke="#000" stroke-width="3" stroke-opacity=".5" paint-order="stroke">{E(UsgsNaipSource.Attribution)}</text>
            </g>
            """);
    }

    public static string Label(FindingType t) => t switch
    {
        FindingType.VegetationEncroachment => "Vegetation encroachment",
        FindingType.DamagedInsulator => "Damaged insulator",
        FindingType.CrossarmDamage => "Crossarm damage",
        FindingType.ThermalHotspot => "Thermal hotspot",
        FindingType.MissingHardware => "Missing hardware",
        FindingType.ConductorDamage => "Conductor damage",
        _ => t.ToString(),
    };

    private const double Zoom = 1.4;

    private const string ThermalScale = """
        <defs><linearGradient id="scale" x1="0" y1="1" x2="0" y2="0"><stop offset="0" stop-color="#1e1033"/><stop offset=".5" stop-color="#c026d3"/><stop offset=".8" stop-color="#f97316"/><stop offset="1" stop-color="#fff"/></linearGradient></defs>
        <rect x="24" y="60" width="10" height="150" fill="url(#scale)" stroke="#fff" stroke-opacity=".4"/>
        """;

    private readonly record struct Box(double X, double Y, double W, double H);

    /// <summary>Keep the box inside the frame, above the caption bar.</summary>
    private static Box Clamp(Box b)
    {
        double x0 = Math.Max(8, b.X), y0 = Math.Max(8, b.Y);
        double x1 = Math.Min(Width - 8, b.X + b.W), y1 = Math.Min(Height - 36, b.Y + b.H);
        return new(x0, y0, Math.Max(20, x1 - x0), Math.Max(20, y1 - y0));
    }

    private static string Detection(Box b, Finding f)
    {
        var c = SeverityColor(f.Severity);
        var tag = Inv($"{Label(f.Type)} {f.Confidence:P0}");
        var tagW = 12 + tag.Length * 7.2;
        var tagX = Math.Min(b.X, Width - 8 - tagW);
        // Above the box unless that would hit the top edge or the SIMULATED badge (top right); then below it.
        var above = b.Y - 22;
        var hitsBadge = above < 40 && tagX + tagW > Width - 126;
        var tagY = above >= 8 && !hitsBadge ? above : Math.Min(b.Y + b.H + 2, Height - 50);
        return Inv($"""
            <g font-family="ui-monospace,Consolas,monospace" font-size="12">
            <rect x="{b.X:0}" y="{b.Y:0}" width="{b.W:0}" height="{b.H:0}" fill="none" stroke="{c}" stroke-width="3"/>
            <rect x="{tagX:0}" y="{tagY:0}" width="{tagW:0}" height="20" fill="{c}"/>
            <text x="{tagX + 6:0}" y="{tagY + 14:0}" fill="#fff" font-weight="700">{E(tag)}</text>
            </g>
            """);
    }

    private static string Overlay(Finding f, bool thermal, bool nadir = false)
    {
        var mode = (nadir ? "NADIR " : "") + (thermal ? "IR" : "RGB");
        var hud = Inv($"{f.StructureLabel} · {f.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z · {f.Location.Lat:0.00000}, {f.Location.Lon:0.00000} · {mode}");
        const string corner = "fill=\"none\" stroke=\"#fff\" stroke-opacity=\".8\" stroke-width=\"2\"";
        return Inv($"""
            <g font-family="ui-monospace,Consolas,monospace" font-size="12">
            <path d="M16 40V16H40M600 16H624V40M624 360V384H600M40 384H16V360" {corner}/>
            <path d="M312 200H328M320 192V208" stroke="#fff" stroke-opacity=".5"/>
            <rect x="0" y="{Height - 28}" width="{Width}" height="28" fill="#000" fill-opacity=".55"/>
            <text x="12" y="{Height - 10}" fill="#e5e7eb">{E(hud)}</text>
            <rect x="{Width - 118}" y="12" width="106" height="22" rx="4" fill="#000" fill-opacity=".6" stroke="#fbbf24"/>
            <text x="{Width - 65}" y="27" fill="#fbbf24" font-weight="700" text-anchor="middle">SIMULATED</text>
            </g>
            """);
    }

    /// <summary>The structure and wires, with hooks to draw each defect. Coordinates are in the 640×400 frame.</summary>
    private sealed class Scene(Random rng, bool thermal)
    {
        private readonly double poleX = 320;
        private readonly double armY = 150;
        private readonly double armHalf = 150;

        public double PoleX => poleX;
        public double ArmY => armY;
        private readonly string sky = thermal ? "#1e1033" : rng.Next(3) switch { 0 => "#7fb3e6", 1 => "#a9c4dc", _ => "#8fc1ea" };
        private readonly string skyLow = thermal ? "#2e1065" : "#dbe9f5";
        private readonly string ground = thermal ? "#3b0764" : rng.Next(2) == 0 ? "#6b7f3a" : "#8a8a4a";
        private readonly string wood = thermal ? "#7c2d92" : "#6b4a2b";
        private readonly string wire = thermal ? "#a855f7" : "#1f2937";
        private readonly string porcelain = thermal ? "#9333ea" : "#cbd5e1";

        private double InsX(int i) => poleX + (i - 1) * (armHalf - 25);

        public string Background() => Inv($"""
            <defs><linearGradient id="sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{sky}"/><stop offset="1" stop-color="{skyLow}"/></linearGradient></defs>
            <rect width="{Width}" height="{Height}" fill="url(#sky)"/>
            <path d="M0 300 Q120 {250 + rng.Next(0, 30)} 240 290 T480 {270 + rng.Next(0, 30)} T640 285 V400 H0Z" fill="{ground}" fill-opacity=".85"/>
            <path d="M0 330 Q200 310 400 335 T640 320 V400 H0Z" fill="{ground}"/>
            """);

        private string Pole(double tiltDeg = 0, bool split = false)
        {
            var sb = new StringBuilder();
            sb.Append(Inv($"""<rect x="{poleX - 9:0}" y="{armY - 30:0}" width="18" height="{Height - armY + 30:0}" fill="{wood}"/>"""));
            sb.Append(Inv($"""<g transform="rotate({tiltDeg:0.0} {poleX:0} {armY:0})"><rect x="{poleX - armHalf:0}" y="{armY - 6:0}" width="{armHalf * 2:0}" height="12" fill="{wood}"/>"""));
            if (split) sb.Append(Inv($"""<path d="M{poleX + 30:0} {armY - 1:0} l18 2 l14 -3 l20 3 l16 -2" stroke="#1c1208" stroke-width="2" fill="none"/>"""));
            sb.Append("</g>");
            return sb.ToString();
        }

        private string Insulator(int i, double dy = 0, string? extra = null)
        {
            var x = InsX(i);
            var y = armY - 6 + dy;
            var sb = new StringBuilder();
            for (var d = 0; d < 4; d++)
                sb.Append(Inv($"""<ellipse cx="{x:0}" cy="{y - 6 - d * 8:0}" rx="10" ry="4" fill="{porcelain}" stroke="#64748b"/>"""));
            if (extra is not null) sb.Append(extra);
            return sb.ToString();
        }

        private string Conductor(int i, double sagExtra = 0, string? stroke = null)
        {
            var x = InsX(i);
            var y = armY - 38;
            var sag = 40 + sagExtra;
            return Inv($"""<path d="M0 {y + sag:0} Q{x / 2:0} {y + sag * 1.6:0} {x:0} {y:0} Q{x + (Width - x) / 2:0} {y + sag * 1.6:0} {Width} {y + sag:0}" fill="none" stroke="{stroke ?? wire}" stroke-width="3"/>""");
        }

        private string Rest(int skipInsulator = -1, int skipConductor = -1)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < 3; i++)
            {
                if (i != skipConductor) sb.Append(Conductor(i));
                if (i != skipInsulator) sb.Append(Insulator(i));
            }
            return sb.ToString();
        }

        public Box Normal(StringBuilder sb)
        {
            sb.Append(Pole()).Append(Rest());
            return new(poleX - 40, armY - 50, 80, 70);
        }

        public Box Vegetation(StringBuilder sb, int severity)
        {
            var left = rng.Next(2) == 0;
            var cx = left ? InsX(0) - 45 : InsX(2) + 45;
            var reach = 60 + severity * 14; // closer to the wires the worse it is
            var top = armY - 38 + 40 + 30 - reach + 40;
            sb.Append(Pole()).Append(Rest());
            sb.Append(Inv($"""<rect x="{cx - 8:0}" y="{top + 60:0}" width="16" height="{Height - top:0}" fill="#4a3520"/>"""));
            for (var k = 0; k < 9; k++)
            {
                var r = 26 + rng.Next(0, 22);
                sb.Append(Inv($"""<circle cx="{cx + rng.Next(-55, 56):0}" cy="{top + rng.Next(0, 90):0}" r="{r}" fill="{(k % 2 == 0 ? "#2f5d1e" : "#3f7a28")}"/>"""));
            }
            return new(cx - 85, top - 30, 170, 150);
        }

        public Box CrackedInsulator(StringBuilder sb)
        {
            var i = rng.Next(3);
            var x = InsX(i);
            var y = armY - 12;
            var crack = Inv($"""<path d="M{x - 8:0} {y - 18:0} l5 4 l-3 5 l6 3 l-2 5 l7 4" stroke="#111" stroke-width="2" fill="none"/><path d="M{x + 4:0} {y - 30:0} l7 -1 l-2 6z" fill="{sky}"/>""");
            sb.Append(Pole());
            for (var k = 0; k < 3; k++)
            {
                sb.Append(Conductor(k));
                sb.Append(k == i ? Insulator(k, extra: crack) : Insulator(k));
            }
            return new(x - 30, y - 50, 60, 60);
        }

        public Box LeaningCrossarm(StringBuilder sb, int severity)
        {
            var tilt = (rng.Next(2) == 0 ? -1 : 1) * (3 + severity * 1.5);
            sb.Append(Pole(tilt, split: severity >= 3));
            sb.Append(Inv($"""<g transform="rotate({tilt:0.0} {poleX:0} {armY:0})">""")).Append(Rest()).Append("</g>");
            return new(poleX - armHalf - 10, armY - 70, armHalf * 2 + 20, 110);
        }

        public Box Hotspot(StringBuilder sb, int severity)
        {
            var i = rng.Next(3);
            var x = InsX(i);
            var y = armY - 38;
            var r = 10 + severity * 4;
            sb.Append(Pole()).Append(Rest());
            sb.Append(Inv($"""
                <defs><radialGradient id="hot"><stop offset="0" stop-color="#fff"/><stop offset=".35" stop-color="#fde047"/><stop offset=".7" stop-color="#f97316"/><stop offset="1" stop-color="#f97316" stop-opacity="0"/></radialGradient></defs>
                <circle cx="{x:0}" cy="{y:0}" r="{r * 2.2:0}" fill="url(#hot)"/>
                <g font-family="ui-monospace,Consolas,monospace" font-size="10" fill="#fde047"><text x="{x - r * 2:0}" y="{y + r * 2.4 + 14:0}">ΔT +{12 + severity * 9} °C</text></g>
                """));
            return new(x - r * 2.4, y - r * 2.4, r * 4.8, r * 4.8);
        }

        public Box MissingInsulator(StringBuilder sb)
        {
            var i = rng.Next(3);
            var x = InsX(i);
            sb.Append(Pole()).Append(Rest(skipInsulator: i, skipConductor: i));
            sb.Append(Conductor(i, sagExtra: 28));
            sb.Append(Inv($"""<rect x="{x - 12:0}" y="{armY - 42:0}" width="24" height="36" fill="none" stroke="#fff" stroke-opacity=".7" stroke-dasharray="4 3"/>"""));
            return new(x - 34, armY - 56, 68, 92);
        }

        public Box FrayedConductor(StringBuilder sb)
        {
            var i = rng.Next(3);
            var x = InsX(i) + (i == 0 ? 90 : -90);
            var y = armY - 38 + 40 * 0.55 + 30;
            sb.Append(Pole()).Append(Rest());
            var strands = new StringBuilder();
            for (var k = 0; k < 7; k++)
                strands.Append(Inv($"""<path d="M{x + rng.Next(-14, 15):0} {y:0} l{rng.Next(-12, 13)} {rng.Next(-18, -4)}" stroke="#94a3b8" stroke-width="1.5"/>"""));
            sb.Append(Inv($"""<ellipse cx="{x:0}" cy="{y:0}" rx="16" ry="7" fill="none" stroke="#94a3b8" stroke-width="2"/>""")).Append(strands);
            return new(x - 38, y - 34, 76, 54);
        }
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>FNV-1a: string.GetHashCode is randomized per process, and the image must not change between requests.</summary>
    private static int StableSeed(string s)
    {
        var h = 2166136261u;
        foreach (var ch in s) h = (h ^ ch) * 16777619u;
        return (int)(h & 0x7fffffff);
    }
}
