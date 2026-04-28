using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

public static class AddressableHelper
{
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