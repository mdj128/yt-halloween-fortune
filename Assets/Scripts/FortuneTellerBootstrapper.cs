using UnityEngine;

/// <summary>
/// Ensures a FortuneTellerController exists when the game launches.
/// </summary>
public static class FortuneTellerBootstrapper
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Initialise()
    {
        if (Object.FindObjectOfType<FortuneTellerController>() != null)
        {
            return;
        }

        var go = new GameObject("FortuneTellerManager");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<AudioSource>();
        go.AddComponent<FortuneTellerController>();
    }
}
