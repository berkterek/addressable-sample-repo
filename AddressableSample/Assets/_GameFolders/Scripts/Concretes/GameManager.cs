using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

public class GameManager : MonoBehaviour
{
    [Header("Addressables")]
    [SerializeField] private string levelLabel = "Level";
    [SerializeField] private string levelAddressPrefix = "Level_";
    [SerializeField] private int defaultLevelIndex = 1;

    [Header("Remote Config")]
    [SerializeField] private EditorAddressablesMode editorAddressablesMode = EditorAddressablesMode.Local;
    [SerializeField] private int remoteConfigRequestTimeoutSeconds = 15;

    [Header("References")]
    [SerializeField] private UiManager uiManager;

    [Header("Debug")]
    [SerializeField] private bool logMemoryUsage = true;

    [Header("Runtime Level Info")]
    [SerializeField] private int totalLevelCount;
    [SerializeField] private int currentLevelIndex;
    [SerializeField] private string currentLevelAddress;
    [SerializeField] private string currentLevelSceneName;
    [SerializeField] private string currentLevelScenePath;
    [SerializeField] private bool currentLevelSceneLoaded;
    [SerializeField] private bool currentLevelSceneActive;
    [SerializeField] private bool isBusy;

    private SceneInstance currentLevelSceneInstance;
    private Scene currentLevelScene;
    private RemoteConfigLoader remoteConfigLoader;
    private readonly List<string> levelAddresses = new();

    public int TotalLevelCount => totalLevelCount;
    public int CurrentLevelIndex => currentLevelIndex;
    public string CurrentLevelAddress => currentLevelAddress;
    public Scene CurrentLevelScene => currentLevelScene;
    public bool IsBusy => isBusy;
    public bool HasCurrentLevelLoaded => currentLevelScene.IsValid() && currentLevelScene.isLoaded;
    public bool HasNextLevel => GetCurrentLevelListIndex() >= 0 && GetCurrentLevelListIndex() < levelAddresses.Count - 1;
    public string NextLevelAddress => HasNextLevel ? levelAddresses[GetCurrentLevelListIndex() + 1] : string.Empty;
    public int NextLevelIndex => HasNextLevel ? GetLevelIndexFromAddress(NextLevelAddress) : 0;

    private void Awake()
    {
        if (uiManager == null)
        {
            uiManager = FindFirstObjectByType<UiManager>();
        }

        remoteConfigLoader = new RemoteConfigLoader(remoteConfigRequestTimeoutSeconds);
        remoteConfigLoader.StatusChanged += HandleRemoteConfigStatusChanged;
        remoteConfigLoader.CatalogLoaded += HandleRemoteCatalogLoaded;
    }

    private void Start()
    {
        InitializeAsync().Forget();
    }

    private void OnDestroy()
    {
        if (remoteConfigLoader == null)
        {
            return;
        }

        remoteConfigLoader.StatusChanged -= HandleRemoteConfigStatusChanged;
        remoteConfigLoader.CatalogLoaded -= HandleRemoteCatalogLoaded;
    }

    public async UniTask StartCurrentLevelAsync()
    {
        if (isBusy || totalLevelCount <= 0)
        {
            return;
        }

        if (HasCurrentLevelLoaded)
        {
            uiManager?.ShowGame(this);
            return;
        }

        await LoadLevelAsync(currentLevelAddress);
    }

    public async UniTask ReturnToMenuAsync()
    {
        if (isBusy)
        {
            return;
        }

        try
        {
            isBusy = true;
            uiManager?.ShowLoading($"Unloading Level {currentLevelIndex}...");
            await UnloadCurrentLevelAsync();
            uiManager?.ShowMenu(this);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            ClearCurrentLevelSceneInfo();
            uiManager?.ShowMenu(this);
        }
        finally
        {
            isBusy = false;
            uiManager?.Refresh(this);
        }
    }

    public async UniTask LoadNextLevelAsync()
    {
        if (isBusy || totalLevelCount <= 0)
        {
            return;
        }

        if (!HasNextLevel)
        {
            return;
        }

        await LoadLevelAsync(NextLevelAddress);
    }

    private async UniTaskVoid InitializeAsync()
    {
        try
        {
            isBusy = true;
            uiManager?.Initialize(this);

            if (ShouldLoadRemoteCatalog())
            {
                uiManager?.ShowLoading("Loading remote config...");
                await remoteConfigLoader.LoadAsync(this.GetCancellationTokenOnDestroy());
            }
            else
            {
                Debug.Log("Using local Editor Addressables catalog.");
            }

            uiManager?.ShowLoading("Reading levels...");
            LogMemory("Before Read Levels");
            levelAddresses.Clear();
            levelAddresses.AddRange(await AddressableHelper.GetUniqueAddressesByLabelAsync(levelLabel));
            levelAddresses.Sort(CompareLevelAddresses);
            totalLevelCount = levelAddresses.Count;
            currentLevelAddress = GetDefaultLevelAddress();
            currentLevelIndex = GetLevelIndexFromAddress(currentLevelAddress);
            ClearCurrentLevelSceneInfo();

            Debug.Log($"Total level count from Addressables label '{levelLabel}': {totalLevelCount}. Addresses: {string.Join(", ", levelAddresses)}");
            LogMemory("After Read Levels");
            uiManager?.ShowMenu(this);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            uiManager?.ShowLoading("Level data failed.");
        }
        finally
        {
            isBusy = false;
            uiManager?.Refresh(this);
        }
    }

    private async UniTask LoadLevelAsync(string levelAddress)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(levelAddress))
            {
                throw new InvalidOperationException("Level address is empty.");
            }

            isBusy = true;
            var levelIndex = GetLevelIndexFromAddress(levelAddress);
            uiManager?.ShowLoading($"Loading Level {levelIndex}...");
            LogMemory($"Before Load {levelAddress}");

            await UnloadCurrentLevelAsync();

            currentLevelIndex = levelIndex;
            currentLevelAddress = levelAddress;
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
            LogMemory($"After Load {currentLevelAddress}");

            uiManager?.ShowGame(this);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            uiManager?.ShowMenu(this);
        }
        finally
        {
            isBusy = false;
            uiManager?.Refresh(this);
        }
    }

    private async UniTask UnloadCurrentLevelAsync()
    {
        if (!HasCurrentLevelLoaded)
        {
            ClearCurrentLevelSceneInfo();
            return;
        }

        Debug.Log($"Unloading current level scene: {currentLevelScene.name}");
        LogMemory($"Before Unload {currentLevelScene.name}");
        await AddressableHelper.UnloadSceneAsync(currentLevelSceneInstance);
        await Resources.UnloadUnusedAssets();
        GC.Collect();
        LogMemory("After Unload And Cleanup");

        ClearCurrentLevelSceneInfo();
    }

    private string GetDefaultLevelAddress()
    {
        if (levelAddresses.Count == 0)
        {
            return string.Empty;
        }

        var preferredAddress = $"{levelAddressPrefix}{defaultLevelIndex}";
        return levelAddresses.Contains(preferredAddress) ? preferredAddress : levelAddresses[0];
    }

    private int GetCurrentLevelListIndex()
    {
        return string.IsNullOrWhiteSpace(currentLevelAddress) ? -1 : levelAddresses.IndexOf(currentLevelAddress);
    }

    private int GetLevelIndexFromAddress(string levelAddress)
    {
        if (string.IsNullOrWhiteSpace(levelAddress) || !levelAddress.StartsWith(levelAddressPrefix, StringComparison.Ordinal))
        {
            return 0;
        }

        var suffix = levelAddress.Substring(levelAddressPrefix.Length);
        return int.TryParse(suffix, out var levelIndex) ? levelIndex : 0;
    }

    private int CompareLevelAddresses(string left, string right)
    {
        var leftIndex = GetLevelIndexFromAddress(left);
        var rightIndex = GetLevelIndexFromAddress(right);
        if (leftIndex > 0 && rightIndex > 0)
        {
            return leftIndex.CompareTo(rightIndex);
        }

        if (leftIndex > 0)
        {
            return -1;
        }

        if (rightIndex > 0)
        {
            return 1;
        }

        return string.Compare(left, right, StringComparison.Ordinal);
    }

    private bool ShouldLoadRemoteCatalog()
    {
#if UNITY_EDITOR
        return editorAddressablesMode == EditorAddressablesMode.Remote;
#else
        return true;
#endif
    }

    private void HandleRemoteConfigStatusChanged(string status)
    {
        uiManager?.ShowLoading(status);
    }

    private void HandleRemoteCatalogLoaded(string catalogUrl)
    {
        Debug.Log($"Remote Addressables catalog loaded: {catalogUrl}");
    }

    private void RefreshCurrentLevelSceneInfo()
    {
        currentLevelSceneLoaded = currentLevelScene.IsValid() && currentLevelScene.isLoaded;
        currentLevelSceneActive = currentLevelScene.IsValid() && SceneManager.GetActiveScene() == currentLevelScene;
        currentLevelSceneName = currentLevelScene.IsValid() ? currentLevelScene.name : string.Empty;
        currentLevelScenePath = currentLevelScene.IsValid() ? currentLevelScene.path : string.Empty;
    }

    private void ClearCurrentLevelSceneInfo()
    {
        currentLevelScene = default;
        currentLevelSceneInstance = default;
        currentLevelSceneName = string.Empty;
        currentLevelScenePath = string.Empty;
        currentLevelSceneLoaded = false;
        currentLevelSceneActive = false;
    }

    private void LogMemory(string label)
    {
        if (!logMemoryUsage)
        {
            return;
        }

        Debug.Log(
            $"[Memory] {label} | Allocated: {FormatBytes(Profiler.GetTotalAllocatedMemoryLong())} | Reserved: {FormatBytes(Profiler.GetTotalReservedMemoryLong())} | Unused Reserved: {FormatBytes(Profiler.GetTotalUnusedReservedMemoryLong())} | Mono Used: {FormatBytes(Profiler.GetMonoUsedSizeLong())} | Mono Heap: {FormatBytes(Profiler.GetMonoHeapSizeLong())}");
    }

    private static string FormatBytes(long bytes)
    {
        const double megabyte = 1024d * 1024d;
        return $"{bytes / megabyte:0.00} MB";
    }
}

public enum EditorAddressablesMode
{
    Local,
    Remote
}
