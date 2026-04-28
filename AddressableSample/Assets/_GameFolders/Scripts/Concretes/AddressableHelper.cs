using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

public static class AddressableHelper
{
    public static async UniTask<int> GetLocationCountAsync(
        object key,
        Type type = null,
        CancellationToken cancellationToken = default)
    {
        var locationsHandle = Addressables.LoadResourceLocationsAsync(key, type);
        try
        {
            await WaitForHandle(locationsHandle, progress: null, cancellationToken);
            return locationsHandle.Result?.Count ?? 0;
        }
        finally
        {
            Addressables.Release(locationsHandle);
        }
    }

    public static UniTask<int> GetLabelCountAsync(
        string label,
        Type type = null,
        CancellationToken cancellationToken = default)
    {
        return GetLocationCountAsync(label, type, cancellationToken);
    }

    public static async UniTask DownloadDependenciesAsync(
        object key,
        IProgress<float> progress = null,
        CancellationToken cancellationToken = default)
    {
        var sizeHandle = Addressables.GetDownloadSizeAsync(key);
        try
        {
            await WaitForHandle(sizeHandle, progress: null, cancellationToken);

            if (sizeHandle.Result <= 0)
            {
                progress?.Report(1f);
                return;
            }
        }
        finally
        {
            Addressables.Release(sizeHandle);
        }

        var downloadHandle = Addressables.DownloadDependenciesAsync(key);
        try
        {
            await WaitForHandle(downloadHandle, progress, cancellationToken);
        }
        finally
        {
            Addressables.Release(downloadHandle);
        }
    }

    public static async UniTask<GameObject> DownloadAndInstantiateAsync(
        string address,
        Transform parent = null,
        bool instantiateInWorldSpace = false,
        IProgress<float> progress = null,
        CancellationToken cancellationToken = default)
    {
        return await DownloadAndInstantiateAsync(
            (object)address,
            parent,
            instantiateInWorldSpace,
            progress,
            cancellationToken);
    }

    public static async UniTask<GameObject> DownloadAndInstantiateAsync(
        AssetReferenceGameObject assetReference,
        Transform parent = null,
        bool instantiateInWorldSpace = false,
        IProgress<float> progress = null,
        CancellationToken cancellationToken = default)
    {
        if (assetReference == null)
        {
            throw new ArgumentNullException(nameof(assetReference));
        }

        await DownloadDependenciesAsync(assetReference.RuntimeKey, progress, cancellationToken);

        var instantiateHandle = assetReference.InstantiateAsync(parent, instantiateInWorldSpace);
        return await CompleteInstantiateHandle(instantiateHandle, cancellationToken);
    }

    public static async UniTask<GameObject> DownloadAndInstantiateAsync(
        object key,
        Transform parent = null,
        bool instantiateInWorldSpace = false,
        IProgress<float> progress = null,
        CancellationToken cancellationToken = default)
    {
        await DownloadDependenciesAsync(key, progress, cancellationToken);

        var instantiateHandle = Addressables.InstantiateAsync(key, parent, instantiateInWorldSpace);
        return await CompleteInstantiateHandle(instantiateHandle, cancellationToken);
    }

    public static async UniTask<T> DownloadAndInstantiateAsync<T>(
        object key,
        Transform parent = null,
        bool instantiateInWorldSpace = false,
        IProgress<float> progress = null,
        CancellationToken cancellationToken = default)
        where T : Component
    {
        var instance = await DownloadAndInstantiateAsync(
            key,
            parent,
            instantiateInWorldSpace,
            progress,
            cancellationToken);

        if (instance.TryGetComponent(out T component))
        {
            return component;
        }

        ReleaseInstance(instance);
        throw new InvalidOperationException($"{instance.name} does not have a {typeof(T).Name} component.");
    }

    public static bool ReleaseInstance(GameObject instance)
    {
        return instance != null && Addressables.ReleaseInstance(instance);
    }

    public static async UniTask<SceneInstance> DownloadAndLoadSceneAsync(
        string address,
        LoadSceneMode loadMode = LoadSceneMode.Additive,
        bool activateOnLoad = true,
        int priority = 100,
        IProgress<float> progress = null,
        CancellationToken cancellationToken = default)
    {
        return await DownloadAndLoadSceneAsync(
            (object)address,
            loadMode,
            activateOnLoad,
            priority,
            progress,
            cancellationToken);
    }

    public static async UniTask<SceneInstance> DownloadAndLoadSceneAsync(
        object key,
        LoadSceneMode loadMode = LoadSceneMode.Additive,
        bool activateOnLoad = true,
        int priority = 100,
        IProgress<float> progress = null,
        CancellationToken cancellationToken = default)
    {
        await DownloadDependenciesAsync(key, progress, cancellationToken);

        var sceneHandle = Addressables.LoadSceneAsync(key, loadMode, activateOnLoad, priority);
        try
        {
            await WaitForHandle(sceneHandle, progress: null, cancellationToken);
            return sceneHandle.Result;
        }
        catch
        {
            if (sceneHandle.IsValid())
            {
                Addressables.Release(sceneHandle);
            }

            throw;
        }
    }

    private static async UniTask<GameObject> CompleteInstantiateHandle(
        AsyncOperationHandle<GameObject> handle,
        CancellationToken cancellationToken)
    {
        try
        {
            await WaitForHandle(handle, progress: null, cancellationToken);
            return handle.Result;
        }
        catch
        {
            if (handle.IsValid())
            {
                Addressables.Release(handle);
            }

            throw;
        }
    }

    private static async UniTask WaitForHandle<T>(
        AsyncOperationHandle<T> handle,
        IProgress<float> progress,
        CancellationToken cancellationToken)
    {
        await WaitForHandle((AsyncOperationHandle)handle, progress, cancellationToken);
    }

    private static async UniTask WaitForHandle(
        AsyncOperationHandle handle,
        IProgress<float> progress,
        CancellationToken cancellationToken)
    {
        while (!handle.IsDone)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(handle.PercentComplete);
            await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
        }

        progress?.Report(1f);

        if (handle.Status == AsyncOperationStatus.Succeeded)
        {
            return;
        }

        throw handle.OperationException ?? new InvalidOperationException("Addressables operation failed.");
    }
}
