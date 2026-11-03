using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SunLoader
{
    enum Shape { Square, Bar, Circle, Ring, Triangle, Spikes, Line, Effect, Diamond, Plus, Hexagon }

    // Friendly names, categories, descriptions and editor shapes for the game's attacks.
    // Descriptions come from watching the attacks in the game's levels and from their
    // names and settings; the Preview button shows exactly what each one does.
    static class AttackInfo
    {
        public const string LevelSpecific = "Level-specific (risky)";
        public static readonly string[] Categories =
        {
            "Projectiles", "Beams & lasers", "Walls & spikes", "Explosions & circles",
            "Patterns & lines", "Camera & effects", "Level tools", LevelSpecific,
        };

        public record Info(string Category, string Description, Shape Shape);
        public record Preset(string Name, string Type, params (string Key, string Value)[] Params);

        // The Shapes list in the editor: the game's attacks, set up to make a shape.
        public const string ShapesCategory = "Shapes";
        public static readonly Preset[] Shapes =
        {
            new("Diamond", "SquareCorner", ("rotOffset", "45")),
            new("Spinning diamond", "SquareCorner", ("rotOffset", "45"), ("rotSpeed", "1")),
            new("Diamond of spikes", "FxTokyoSkiesDiamond"),
            new("Orbiting diamond", "FxClashDiamond"),
            new("Square", "SquareAppearStay"),
            new("Circle", "BlackHole"),
            new("Ring", "RingShrink"),
            new("Triangle", "TriangleShoot"),
            new("Plus", "fxPlus"),
            new("Cross", "Laser4"),
            new("Star burst", "ExplodiongSpikes"),
            new("Spiky ball", "RoundSpikes"),
            new("Hexagon line", "HexagonLine"),
        };

        static readonly Dictionary<string, Info> known = new()
        {
            // Projectiles
            ["Square"] = new("Projectiles", "A square that flies in from a screen edge and across the screen. Set the edge with modelSide.", Shape.Square),
            ["SquareAppearStay"] = new("Projectiles", "A square that pops up at a spot and stays there until its outro ends.", Shape.Square),
            ["RoundSpikes"] = new("Projectiles", "A spiky ball that appears at a spot and flies off in a direction. direction can be random, e.g. -30+random(60).", Shape.Spikes),
            ["RoundSpikesUnder"] = new("Projectiles", "Same as RoundSpikes, drawn underneath other attacks.", Shape.Spikes),
            ["TriangleShoot"] = new("Projectiles", "A triangle that aims and fires at a player.", Shape.Triangle),
            ["TriangleSquid"] = new("Projectiles", "A squid-like triangle that darts across the screen from one side.", Shape.Triangle),
            ["PyramidSnake"] = new("Projectiles", "A snake of shapes that wiggles across the screen. wiggleSpeed and wiggleAngle shape its path.", Shape.Line),
            ["fxBubble"] = new("Projectiles", "A bubble that drifts, then bursts into spikes.", Shape.Circle),
            ["contraEnemy"] = new("Projectiles", "A linked chain enemy that moves and can bounce off walls.", Shape.Line),
            ["FxShootingStar"] = new("Projectiles", "A fast streak that shoots across the screen at an angle (rotOffset).", Shape.Line),
            ["Dan_Arrow"] = new("Projectiles", "An arrow fired in a direction (rotOffset).", Shape.Triangle),
            ["SpiderLittle"] = new("Projectiles", "A small spider that runs across the screen.", Shape.Triangle),
            ["SpiderLittle2"] = new("Projectiles", "A small spider variant that runs across the screen.", Shape.Triangle),
            ["COD_Solo_Ball"] = new("Projectiles", "A bouncing spiky ball that sprays smaller bullets.", Shape.Spikes),
            ["COD_Trail"] = new("Projectiles", "A spiky ball that moves in a looping trail.", Shape.Spikes),
            ["Pills"] = new("Projectiles", "Pill shapes that hop around to a beat (bpm).", Shape.Square),
            ["SquareWave"] = new("Projectiles", "A wave of squares that travels in a direction.", Shape.Square),

            // Beams & lasers
            ["Beam"] = new("Beams & lasers", "The classic beam: a warning line, then a full-width laser at that height (or width, if modelSide is up/down).", Shape.Bar),
            ["BeamMoving"] = new("Beams & lasers", "A beam that slides across the screen after its warning.", Shape.Bar),
            ["BeamFromAnywhere"] = new("Beams & lasers", "A beam through any point at any angle (rotOffset).", Shape.Bar),
            ["BeamBorderless"] = new("Beams & lasers", "A beam with a set length (height) instead of crossing the whole screen, at any angle.", Shape.Bar),
            ["BeamGroup"] = new("Beams & lasers", "Several beams fanning out from one point (spikes = how many).", Shape.Spikes),
            ["SubStepBeam"] = new("Beams & lasers", "A shorter-warning beam used for quick follow-ups.", Shape.Bar),
            ["LazerRotate"] = new("Beams & lasers", "A thin laser that spins around a point (rotateSpeed).", Shape.Bar),
            ["Laser4"] = new("Beams & lasers", "Four lasers in a cross that can spin around a point.", Shape.Spikes),
            ["Warning"] = new("Beams & lasers", "Just a warning flash on a screen edge; no damage. Good for telegraphing.", Shape.Bar),

            // Walls & spikes
            ["Wall"] = new("Walls & spikes", "A thick wall that slams in from a screen edge (modelSide).", Shape.Bar),
            ["WallWithRound"] = new("Walls & spikes", "A wall with rounded spikes that sweeps in from the side.", Shape.Bar),
            ["WallWithRoundTop"] = new("Walls & spikes", "A spiked wall that comes in from the top.", Shape.Bar),
            ["WallRoundGoesTowardsPlayerOnMsg"] = new("Walls & spikes", "A spiked wall that moves toward the player when triggered.", Shape.Bar),
            ["SubStepWall"] = new("Walls & spikes", "A quicker wall slam from an edge.", Shape.Bar),
            ["SpikeBar"] = new("Walls & spikes", "A long bar of spikes at an angle (rotOffset).", Shape.Bar),
            ["LastTileSpikeBar"] = new("Walls & spikes", "A spike bar variant from the last tile section.", Shape.Bar),
            ["TutorialWallOfSpikes"] = new("Walls & spikes", "A slow wall of spikes from the tutorial.", Shape.Bar),
            ["SawOnRail"] = new("Walls & spikes", "A saw blade that runs along a rail.", Shape.Spikes),
            ["fxPiston"] = new("Walls & spikes", "A piston that punches out.", Shape.Bar),
            ["fxPlus"] = new("Walls & spikes", "A plus-shaped hazard.", Shape.Plus),
            ["SquareCorner"] = new("Walls & spikes", "A big square that grows from a corner area; can rotate (rotOffset, rotSpeed).", Shape.Square),
            ["ExplodiongSpikes"] = new("Walls & spikes", "Spikes that burst out from a point in a ring (spikes = how many).", Shape.Spikes),
            ["ExplodingSpikesBig"] = new("Walls & spikes", "A big, slow burst of spikes.", Shape.Spikes),
            ["CavernSpikes"] = new("Walls & spikes", "Spikes that rise from a screen edge.", Shape.Spikes),
            ["CavernSpikesFast"] = new("Walls & spikes", "Fast spikes that rise from a screen edge.", Shape.Spikes),

            // Explosions & circles
            ["BlackHole"] = new("Explosions & circles", "A circle that warns, then fills in and hurts. size sets how big.", Shape.Circle),
            ["BlackHoleSpectra"] = new("Explosions & circles", "A black hole that grows from size to sizeMax.", Shape.Circle),
            ["Bomb"] = new("Explosions & circles", "A bomb that explodes after its warning.", Shape.Circle),
            ["BombShockwave"] = new("Explosions & circles", "A bomb that explodes into an expanding shockwave.", Shape.Ring),
            ["BombShockwaveGroup"] = new("Explosions & circles", "Several shockwave bombs along an edge.", Shape.Ring),
            ["CircleFromEdges"] = new("Explosions & circles", "A circle that closes in from the screen edges toward a point.", Shape.Ring),
            ["RingShrink"] = new("Explosions & circles", "A ring that shrinks toward its center.", Shape.Ring),
            ["Shockwave"] = new("Explosions & circles", "An expanding shockwave ring.", Shape.Ring),
            ["Wheel"] = new("Explosions & circles", "Balls orbiting a center point like a wheel.", Shape.Ring),
            ["TryThisBlackHole"] = new("Explosions & circles", "A big black hole that moves around.", Shape.Circle),
            ["FlameThrower"] = new("Explosions & circles", "A burst of flame from a point.", Shape.Triangle),

            // Patterns & lines
            ["dnaLine"] = new("Patterns & lines", "A double-helix line of dots.", Shape.Line),
            ["fxLineOfRoundSpikes"] = new("Patterns & lines", "A line of spiky balls (num = how many).", Shape.Line),
            ["fxLineBowser"] = new("Patterns & lines", "A rotating line of spiky balls, like a fire bar.", Shape.Line),
            ["FxSquareLine"] = new("Patterns & lines", "A line of squares that appear one after another.", Shape.Line),
            ["FxSquareLine2"] = new("Patterns & lines", "A line of spiky balls appearing one by one in a direction.", Shape.Line),
            ["FxSquareLineRounded"] = new("Patterns & lines", "A line of rounded squares appearing one by one.", Shape.Line),
            ["FxSpectrumBar"] = new("Patterns & lines", "Bars that pulse like an audio spectrum.", Shape.Bar),
            ["FxAvalanche"] = new("Patterns & lines", "A falling avalanche of shapes.", Shape.Line),
            ["HexagonLine"] = new("Patterns & lines", "A line of hexagons.", Shape.Hexagon),
            ["FxClashDiamond"] = new("Patterns & lines", "Spiky balls moving in a diamond pattern.", Shape.Diamond),
            ["FxClashCannon"] = new("Patterns & lines", "A cannon that fires spiky balls.", Shape.Spikes),
            ["FxTokyoSkiesDiamond"] = new("Patterns & lines", "Spiky balls moving in a diamond.", Shape.Diamond),
            ["FxChaosgame"] = new("Patterns & lines", "A chaotic fractal pattern of points.", Shape.Line),

            // Camera & effects
            ["CamMove"] = new("Camera & effects", "Moves the camera to x/y over the attack's duration.", Shape.Effect),
            ["CamPos"] = new("Camera & effects", "Snaps the camera to a position.", Shape.Effect),
            ["CamBeatShake"] = new("Camera & effects", "Shakes the camera on the beat.", Shape.Effect),
            ["CamBeatEffect"] = new("Camera & effects", "Bumps the camera toward an edge on the beat (modelSide, speed).", Shape.Effect),
            ["CamBReath"] = new("Camera & effects", "Slow in-and-out camera breathing.", Shape.Effect),
            ["SlowDownEffect"] = new("Camera & effects", "Slow-motion effect.", Shape.Effect),
            ["GlitchMusicEffect"] = new("Camera & effects", "Glitches the music.", Shape.Effect),
            ["EffectGrav"] = new("Camera & effects", "Changes gravity/pull on a target in a direction.", Shape.Effect),
            ["EffectVelocity"] = new("Camera & effects", "Pushes a target attack with a velocity (vx, vy).", Shape.Effect),
            ["EffectDeleteAll"] = new("Camera & effects", "Removes every attack on screen.", Shape.Effect),
            ["EffectSendToBackFront"] = new("Camera & effects", "Moves a target attack behind or in front of others.", Shape.Effect),
            ["TrailEffect"] = new("Camera & effects", "Gives a target attack a motion trail.", Shape.Effect),
            ["PivotRotate"] = new("Camera & effects", "Makes a target attack orbit around a point.", Shape.Effect),
            ["PivotRotatePos"] = new("Camera & effects", "Moves a target attack's orbit center.", Shape.Effect),
            ["applyEaseing"] = new("Camera & effects", "Applies an easing curve to a target attack's movement.", Shape.Effect),
            ["Beat"] = new("Camera & effects", "A beat pulse for the background.", Shape.Effect),
            ["TextOnBottom"] = new("Camera & effects", "Shows text at the bottom of the screen.", Shape.Effect),

            // Level tools
            ["CheckPoint"] = new("Level tools", "A checkpoint: players respawn here. Put one every 20-40 seconds.", Shape.Effect),
            ["SpawnMsg"] = new("Level tools", "Sends a message to another attack (target) to trigger it.", Shape.Effect),
            ["SquareWaveMsg"] = new("Level tools", "Triggers a SquareWave.", Shape.Effect),
            ["TryThisBlackHoleMsg"] = new("Level tools", "Triggers a TryThisBlackHole.", Shape.Effect),
            ["EnemyGroupCreator"] = new("Level tools", "Spawns a group of attacks together.", Shape.Effect),
        };

        // Name prefixes of attacks built for one level's boss or set pieces. They often rely on
        // that level's graphics and can do nothing or crash elsewhere.
        static readonly string[] levelPrefixes =
        {
            "Boss", "boss", "LuiKang", "Cavern", "FxLunarWhale", "FxPlesco", "Spectra", "TryThis", "specter",
            "RDE", "Dan_", "Kubbi_", "Spider", "CoolFriends", "FxIntoTheZone", "FxSquareAppearStayPreParadise",
            "Equalizer", "fxBossFinal", "PreviewBeat", "Sevcon", "LavaPlatform", "onTheRun", "borderHardcore",
            "FxTryThis", "WaterDrop", "substep", "Fx", "fx",
        };

        static readonly Dictionary<string, Info> guessed = new();

        public static Info Get(string type)
        {
            if (type != null && known.TryGetValue(type, out var info)) return info;
            if (type != null && guessed.TryGetValue(type, out info)) return info;
            var risky = type != null && levelPrefixes.Any(type.StartsWith);
            info = new(risky ? LevelSpecific : "Patterns & lines",
                risky ? "Part of one level's boss or set piece. It may need that level's graphics, so it can do nothing or crash in other levels. Save before trying it."
                      : "No description yet. Press Preview to see it.",
                Shape.Square);
            if (type != null) guessed[type] = info;
            return info;
        }

        public static bool IsRisky(string type) => Get(type).Category == LevelSpecific;

        static readonly Dictionary<string, string> friendly = new();

        // "BombShockwaveGroup" -> "Bomb Shockwave Group"
        public static string Friendly(string type)
        {
            if (type == null) return "";
            if (friendly.TryGetValue(type, out var cached)) return cached;
            var s = Regex.Replace(type.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ");
            s = s.Replace("Fx ", "").Replace("fx ", "").Trim();
            return friendly[type] = s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : type;
        }

        static readonly Dictionary<string, string> paramHelp = new()
        {
            ["modelSide"] = "Which screen edge it comes from: left, right, up, down (or any).",
            ["size"] = "How big it is. 100 is normal.",
            ["speed"] = "How fast it moves.",
            ["direction"] = "Direction in degrees (0 = right, 90 = down). Can be random, e.g. 0+random(360).",
            ["rotOffset"] = "Rotation in degrees.",
            ["rotSpeed"] = "How fast it spins.",
            ["rotateSpeed"] = "How fast it spins.",
            ["rotationSpeed"] = "How fast it spins.",
            ["rotateItselfSpeed"] = "How fast it spins on itself.",
            ["spikes"] = "How many spikes or pieces.",
            ["num"] = "How many.",
            ["visual"] = "Which graphic to use, e.g. ENEMY_BEAM or ENEMY_ROUND_SPIKES_5.",
            ["autoShakeCam"] = "Shake the camera when it hits (true/false).",
            ["shakeOnIdle"] = "Keep shaking while it's active (true/false).",
            ["prctSpawnOnPlayer"] = "Chance (%) to aim at a player instead of its position.",
            ["showAppearFx"] = "Show the pop-in effect (true/false).",
            ["dontFlashWhite"] = "1 = skip the white flash when it lands.",
            ["placeOnGrid"] = "Snap to the game's grid (true/false).",
            ["text"] = "The text to show.",
            ["target"] = "Name of the attack this one affects.",
            ["bpm"] = "Beats per minute it moves to.",
            ["width"] = "Width.",
            ["height"] = "Height or length.",
            ["limit"] = "Maximum number of pieces.",
            ["wiggleSpeed"] = "How fast it wiggles.",
            ["wiggleAngle"] = "How far it wiggles, in degrees.",
            ["delay"] = "Delay in ms.",
            ["sizeMax"] = "Size it grows to.",
            ["customChoice"] = "A preset variant for this attack.",
        };

        public static string ParamHelp(string key) => paramHelp.TryGetValue(key, out var h) ? h : null;
    }
}
