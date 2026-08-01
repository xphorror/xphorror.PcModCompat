using UnityEngine.Events;

namespace UnityEngine.SceneManagement;

public struct Scene
{
    public string name { get; set; }
    public bool isLoaded { get; set; }
}

public static class SceneManager
{
    public static event UnityAction<Scene>? sceneUnloaded;

    public static Scene GetActiveScene() => new() { name = string.Empty, isLoaded = true };

    public static void CompatRaiseSceneUnloaded(string name)
    {
        sceneUnloaded?.Invoke(new Scene { name = name, isLoaded = false });
    }
}
