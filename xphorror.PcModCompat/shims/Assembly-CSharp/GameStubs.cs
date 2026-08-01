using TMPro;
using UnityEngine;
using UnityEngine.UI;

public enum States
{
    Start,
    Countdown,
    PlayerControl,
    Fail,
    Fail2,
    Won
}

public enum HitMargin
{
    TooEarly,
    VeryEarly,
    EarlyPerfect,
    Perfect,
    LatePerfect,
    VeryLate,
    TooLate,
    Multipress,
    FailMiss,
    FailOverload,
    Auto,
    OverPress
}

public enum LevelEventType
{
    Unknown,
    SetSpeed,
    Twirl,
    Checkpoint
}

public enum Platform
{
    None,
    Linux,
    Mac,
    Windows,
    Android,
    iOS,
    Switch,
    WebGL
}

public enum PlanetColorPreset
{
    DefaultRed,
    DefaultBlue,
    Orange,
    LightBlue,
    Green,
    Pink,
    Purple,
    Grass,
    PastelPink,
    PastelBlue,
    Violet,
    Aqua,
    Black,
    White,
    Gold,
    Rainbow,
    Crimson,
    Maroon,
    Jungle,
    Vine,
    Cyan,
    Teal,
    Jester,
    Stone,
    Rust,
    Metal,
    Overseer,
    NBPurple,
    NBYellow,
    Custom,
    CoopRed,
    CoopBlue,
    CoopYellow,
    CoopGreen,
    CoopOrange,
    CoopPink,
    CoopCyan,
    CoopPurple,
    TransPink,
    TransBlue
}

public struct PlanetColor
{
    public PlanetColorPreset preset;
    public Color? customColor;

    public PlanetColor(PlanetColorPreset preset)
    {
        this.preset = preset;
        customColor = null;
    }

    public PlanetColor(Color customColor)
    {
        preset = PlanetColorPreset.Custom;
        this.customColor = customColor;
    }

    public Color ToRealColor() => customColor ?? preset.GetColor();
}

public static class RDUtils
{
    public static Color GetColor(this PlanetColorPreset preset) => RDConstants.data.GetPlanetColor(preset);
}

public class LevelEvent
{
    public LevelEventType eventType;
}

public static class RDC
{
    public static bool auto { get; set; }
    public static bool noFail { get; set; }
}

public class RDConstants
{
    public const float HitWindow = 1f;
    public static RDConstants data { get; } = new();
    public TMP_FontAsset chineseFontTMPro = new();
    public Texture2D tex_planetWhite = new();

    public Color GetPlanetColor(PlanetColorPreset preset) => preset switch
    {
        PlanetColorPreset.DefaultRed or PlanetColorPreset.CoopRed => Color.red,
        PlanetColorPreset.DefaultBlue or PlanetColorPreset.CoopBlue => Color.blue,
        PlanetColorPreset.CoopYellow or PlanetColorPreset.Gold => Color.yellow,
        PlanetColorPreset.CoopGreen or PlanetColorPreset.Green => Color.green,
        _ => Color.white
    };
}

public static class GCS
{
    public static bool practiceMode;
}

public static class ADOBase
{
    public static scrController? controllerOverride;
    public static scnEditor? editorOverride;
    public static scrLoader loaderOverride = new();
    public static ADOBaseLevelMaker lmOverride = new();
    public static Platform platform = Platform.Android;
    public static bool isLevelSelectOverride;
    public static bool isOfficialLevel;
    public static string sceneName = string.Empty;
    public static string customLevelPath = string.Empty;

    public static scrConductor conductor => scrConductor.instance;
    public static scrLoader loader => loaderOverride;
    public static scrController? controller => controllerOverride ?? scrController.instance;
    public static ADOBaseLevelMaker lm => lmOverride;
    public static RDConstants gc => RDConstants.data;
    public static scnCLS? cls => scnCLS.instance;
    public static scnEditor? editor => editorOverride ?? scnEditor.instance;
    public static scnGame? customLevel => scnGame.instance;
    public static bool isLevelEditor => editor != null;
    public static bool isScnGame => customLevel != null && !isLevelEditor;
    public static bool isCLS => cls != null;
    public static bool isLevelSelect => isLevelSelectOverride || scnLevelSelect.instance != null;

    public static void LoadScene(string name) => sceneName = name;
}

public class scrLoader
{
    public static scrLoader instance { get; set; } = new();
    public void LoadScene(string name) => ADOBase.sceneName = name;
}

public class ADOBaseLevelMaker
{
    public List<scrFloor> listFloors = new();
}

public class scrController : MonoBehaviour
{
    private static scrController? _instance;
    public static bool coopMode;
    public static int checkpointsUsed;
    public static string currentWorldString = string.Empty;

    public int currentSeqID;
    public double speed = 1;
    public States state;
    private bool _paused;
    public scrMistakesManager mistakesManager = new();
    public PlanetarySystem planetarySystem;
    public scrPlayer playerOne;
    public scrFloor firstFloor = new();
    public scrFloor currFloor => planetarySystem.chosenPlanet.currfloor;
    public float percentComplete => ADOBase.lm.listFloors.Count == 0
        ? 0f
        : (float)(currentSeqID + 1) / ADOBase.lm.listFloors.Count;
    public TextMeshProUGUI? txtLevelName;
    public List<scrPlanet> allPlanets = new();

    public scrController()
    {
        firstFloor = new scrFloor();
        playerOne = new scrPlayer { playerID = 0, alive = true };
        var planet = new scrPlanet { playerID = 0, player = playerOne, currfloor = firstFloor };
        planetarySystem = new PlanetarySystem
        {
            chosenPlanet = planet,
            planetBlue = planet,
            planetRed = planet,
            planetGreen = planet,
            allPlanets = [planet]
        };
        planet.planetarySystem = planetarySystem;
        playerOne.planetarySystem = planetarySystem;
        allPlanets = [planet];
    }

    public static scrController? instance
    {
        get => _instance;
        set => _instance = value;
    }

    public bool paused
    {
        get => _paused;
        set => _paused = value;
    }

    public void Hit() { }
    public void Awake_Rewind() { }
    public void StartLoadingScene() { }
}

public class scrConductor : MonoBehaviour
{
    private static scrConductor? _instance = new();

    public AudioSource song = new() { pitch = 1f };
    public AudioSource? song2;
    public AudioSource? song3;
    public scnEditor? editorComponent;
    public scnCLS? CLSComponent;
    public scnGame? customLevelComponent;
    public double addoffset;
    public float bpm = 100f;
    public bool isGameWorld = true;
    public double dspTime;
    public double dspTimeSong;

    public static scrConductor instance
    {
        get => _instance ??= new scrConductor();
        set => _instance = value;
    }

    public double songposition_minusi { get; set; }
    public double songposition_minusv => songposition_minusi + calibration_i - calibration_v;
    public static float calibration_i { get; set; }
    public static float calibration_v { get; set; }
}

public class scnGame : MonoBehaviour
{
    public static scnGame? instance;
    public string levelPath = string.Empty;
    public void Play(int seqID) { }
}

public class scnCLS : MonoBehaviour
{
    public static scnCLS? instance;
}

public class scrPressToStart : MonoBehaviour
{
    public void ShowText() { }
}

public class scrUIController : MonoBehaviour
{
    public void WipeToBlack() { }
}

public class scnEditor : MonoBehaviour
{
    public static scnEditor? instance;
    public Image autoImage = new();
    public void ResetScene() { }
    public void OttoUpdate() { }
}

public class scnLevelSelect : MonoBehaviour
{
    public static scnLevelSelect? instance;
    public static bool isLevelSelect;
    public void RainbowMode() { }
    public void EnbyMode() { }
}

public class scrFloor : MonoBehaviour
{
    public int seqID;
    public bool auto;
    public double entryTime;
    public float marginScale = 1f;
    public int countdownTicks;
    public scrFloor? nextfloor;
    public FloorRenderer floorRenderer = new();
    public void Start() { }
    public void SetTileColor() { }
}

public class FloorRenderer
{
    public Color color { get; set; } = Color.white;
}

public class scrPlanet : MonoBehaviour
{
    public PlanetarySystem planetarySystem = null!;
    public PlanetRenderer planetRenderer = new();
    public scrFloor currfloor = new();
    public scrPlayer player = null!;
    public int playerID;
    public void Start() { }
    public void MoveToNextFloor() { }
    public void LoadPlanetColor() { }
}

public class PlanetRenderer
{
    public Sprite? sprite { get; set; }
    public PlanetColor planetColor = new(PlanetColorPreset.DefaultRed);
    public void SetPlanetColor(Color color) { }
    public void SetCoreColor(Color color) { }
    public void SetTailColor(Color color) { }
    public void SetRingColor(Color color) { }
    public void SetFaceColor(Color color) { }
}

public class PlanetarySystem : MonoBehaviour
{
    public double speed = 1;
    public scrPlanet chosenPlanet = null!;
    public scrPlanet planetBlue = null!;
    public scrPlanet planetRed = null!;
    public scrPlanet planetGreen = null!;
    public scrPlanet[] allPlanets = [];
    public void RainbowMode() { }
    public void EnbyMode() { }
}

public class scrMistakesManager : MonoBehaviour
{
    public static int[] hitMarginsCount = new int[8];
    public static scrMarginTracker[] marginTrackers = [new()];

    public float percentAcc = 100;
    public float percentXAcc = 100;

    public void AddHit(HitMargin hit) { }
    public void Reset() { }
    public void CalculatePercentAcc() { }
    public void SetPlayerCount() { }
}

public class scrMarginTracker : MonoBehaviour
{
    public int[] hitMarginsCount = new int[8];
    public float percentAcc = 100;
    public float percentXAcc = 100;

    public void AddHit(HitMargin hit) { }
    public void Reset() { }
    public void CalculatePercentAcc() { }
}

public class scrPlayer : MonoBehaviour
{
    public int playerID;
    public bool alive = true;
    public PlanetarySystem planetarySystem = null!;
    public void Hit(HitMargin hit) { }
    public void Die() { }
}

public static class scrPlayerManager
{
    public static int playerCount = 1;
    public static scrPlayerManagerInstance instance = new();
    public static PlanetColor[] playerColors = [new(PlanetColorPreset.CoopRed), new(PlanetColorPreset.CoopBlue), new(PlanetColorPreset.CoopYellow), new(PlanetColorPreset.CoopGreen)];
}

public class scrPlayerManagerInstance
{
    public scrMistakesManager mistakesManager = new();
    public scrPlayer[] allPlayers = [new() { alive = true }];
    public scrPlayer[] players = [new() { alive = true }];
}

public class scrLevelMaker : MonoBehaviour
{
    public static scrLevelMaker? instance;
    public List<scrFloor> listFloors = new();
}

public class ffxCheckpoint : MonoBehaviour;

public class scrShowIfDebug : MonoBehaviour
{
    public Text? txt;
    public void Awake() { }
    public void Update() { }
}

public class scrLogoText : MonoBehaviour
{
    public static scrLogoText? instance;
    public void Awake() { }
    public void UpdateColors() { }
    public void LateUpdate() { }
    public void ColorLogo(Color color, bool isFire) { }
}

public static class scrMisc
{
    public static HitMargin GetHitMargin(float hitangle, float refangle, bool isCW, float bpmTimesSpeed, float conductorPitch)
        => HitMargin.Perfect;
}

public class RDCheatCode
{
    private readonly string _code;
    public RDCheatCode(string code) => _code = code;
    public bool CheckCheatCode() => false;
    public override string ToString() => _code;
}

namespace MonsterLove.StateMachine
{
    public class StateBehaviour : UnityEngine.MonoBehaviour
    {
        public void ChangeState(Enum newState) { }
    }
}
