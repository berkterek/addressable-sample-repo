using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

public class GameManager : MonoBehaviour
{
    [Header("Addressables")]
    [SerializeField] private string levelLabel = "Level";
    [SerializeField] private string levelAddressPrefix = "Level_";
    [SerializeField] private int defaultLevelIndex = 1;

    [Header("Runtime Level Info")]
    [SerializeField] private int totalLevelCount;
    [SerializeField] private int currentLevelIndex;
    [SerializeField] private string currentLevelAddress;
    [SerializeField] private string currentLevelSceneName;
    [SerializeField] private string currentLevelScenePath;
    [SerializeField] private bool currentLevelSceneLoaded;
    [SerializeField] private bool currentLevelSceneActive;

    private SceneInstance currentLevelSceneInstance;
    private Scene currentLevelScene;

    public int TotalLevelCount => totalLevelCount;
    public int CurrentLevelIndex => currentLevelIndex;
    public string CurrentLevelAddress => currentLevelAddress;
    public Scene CurrentLevelScene => currentLevelScene;

    private void Start()
    {
        InitializeAsync().Forget();
    }

    private async UniTaskVoid InitializeAsync()
    {
        try
        {
            totalLevelCount = await AddressableHelper.GetLabelCountAsync(levelLabel);
            Debug.Log($"Total level count from Addressables label '{levelLabel}': {totalLevelCount}");

            currentLevelIndex = defaultLevelIndex;
            currentLevelAddress = GetLevelAddress(currentLevelIndex);
            currentLevelSceneInstance = await AddressableHelper.DownloadAndLoadSceneAsync(
                currentLevelAddress,
                LoadSceneMode.Additive);

            currentLevelScene = currentLevelSceneInstance.Scene;
            if (currentLevelScene.IsValid())
            {
                SceneManager.SetActiveScene(currentLevelScene);
            }

            RefreshCurrentLevelSceneInfo();
            Debug.Log(
                $"Current level loaded. Index: {currentLevelIndex}, Address: {currentLevelAddress}, Scene: {currentLevelSceneName}, Loaded: {currentLevelSceneLoaded}, Active: {currentLevelSceneActive}");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
    }

    private string GetLevelAddress(int levelIndex)
    {
        return $"{levelAddressPrefix}{levelIndex}";
    }

    private void RefreshCurrentLevelSceneInfo()
    {
        currentLevelSceneLoaded = currentLevelScene.IsValid() && currentLevelScene.isLoaded;
        currentLevelSceneActive = currentLevelScene.IsValid() && SceneManager.GetActiveScene() == currentLevelScene;
        currentLevelSceneName = currentLevelScene.IsValid() ? currentLevelScene.name : string.Empty;
        currentLevelScenePath = currentLevelScene.IsValid() ? currentLevelScene.path : string.Empty;
    }
}
