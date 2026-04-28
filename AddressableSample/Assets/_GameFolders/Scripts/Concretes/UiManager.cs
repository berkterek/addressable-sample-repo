using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class UiManager : MonoBehaviour
{
    [Header("Roots")]
    [SerializeField] private CanvasGroup menuRoot;
    [SerializeField] private CanvasGroup gameRoot;
    [SerializeField] private CanvasGroup loadingRoot;

    [Header("Buttons")]
    [SerializeField] private Button startButton;
    [SerializeField] private Button backButton;
    [SerializeField] private Button nextButton;

    [Header("Texts")]
    [SerializeField] private TextMeshProUGUI startButtonText;
    [SerializeField] private TextMeshProUGUI nextButtonText;
    [SerializeField] private TextMeshProUGUI loadingText;

    private GameManager gameManager;

    private void Awake()
    {
        BindMissingReferences();
        ShowMenu(gameManager);
    }

    private void OnDestroy()
    {
        RemoveButtonListeners();
    }

    public void Initialize(GameManager manager)
    {
        gameManager = manager;

        RemoveButtonListeners();
        if (startButton != null)
        {
            startButton.onClick.AddListener(() => gameManager.StartCurrentLevelAsync().Forget());
        }

        if (backButton != null)
        {
            backButton.onClick.AddListener(() => gameManager.ReturnToMenuAsync().Forget());
        }

        if (nextButton != null)
        {
            nextButton.onClick.AddListener(() => gameManager.LoadNextLevelAsync().Forget());
        }

        Refresh(gameManager);
    }

    public void ShowMenu(GameManager manager)
    {
        gameManager = manager;
        SetRoot(menuRoot, true);
        SetRoot(gameRoot, false);
        SetRoot(loadingRoot, false);
        Refresh(gameManager);
    }

    public void ShowGame(GameManager manager)
    {
        gameManager = manager;
        SetRoot(menuRoot, false);
        SetRoot(gameRoot, true);
        SetRoot(loadingRoot, false);
        Refresh(gameManager);
    }

    public void ShowLoading(string message)
    {
        SetRoot(menuRoot, false);
        SetRoot(gameRoot, false);
        SetRoot(loadingRoot, true);

        if (loadingText != null)
        {
            loadingText.text = message;
        }
    }

    public void Refresh(GameManager manager)
    {
        gameManager = manager;

        var currentLevelIndex = gameManager != null ? gameManager.CurrentLevelIndex : 1;
        var totalLevelCount = gameManager != null ? gameManager.TotalLevelCount : 0;
        var isBusy = gameManager != null && gameManager.IsBusy;
        var hasNextLevel = gameManager != null && gameManager.HasNextLevel;

        if (startButtonText != null)
        {
            startButtonText.text = $"Level {currentLevelIndex} Start";
        }

        if (nextButtonText != null && hasNextLevel)
        {
            nextButtonText.text = gameManager.NextLevelAddress;
        }

        if (startButton != null)
        {
            startButton.interactable = !isBusy && totalLevelCount > 0;
        }

        if (backButton != null)
        {
            backButton.interactable = !isBusy;
        }

        if (nextButton != null)
        {
            nextButton.gameObject.SetActive(hasNextLevel);
            nextButton.interactable = !isBusy && hasNextLevel;
        }
    }

    private void BindMissingReferences()
    {
        menuRoot = menuRoot != null ? menuRoot : FindCanvasGroup("MenuRoot");
        gameRoot = gameRoot != null ? gameRoot : FindCanvasGroup("InGameRoot");
        loadingRoot = loadingRoot != null ? loadingRoot : FindCanvasGroup("InLoadingRoot");

        startButton = startButton != null ? startButton : FindButton("StartButton");
        backButton = backButton != null ? backButton : FindButton("BackButton");
        nextButton = nextButton != null ? nextButton : FindButton("NextButton");

        startButtonText = startButtonText != null ? startButtonText : startButton?.GetComponentInChildren<TextMeshProUGUI>(true);
        nextButtonText = nextButtonText != null ? nextButtonText : nextButton?.GetComponentInChildren<TextMeshProUGUI>(true);
        loadingText = loadingText != null ? loadingText : FindText("LoadingText");
    }

    private void RemoveButtonListeners()
    {
        if (startButton != null)
        {
            startButton.onClick.RemoveAllListeners();
        }

        if (backButton != null)
        {
            backButton.onClick.RemoveAllListeners();
        }

        if (nextButton != null)
        {
            nextButton.onClick.RemoveAllListeners();
        }
    }

    private CanvasGroup FindCanvasGroup(string targetName)
    {
        var target = FindChild(targetName);
        return target != null ? target.GetComponent<CanvasGroup>() : null;
    }

    private Button FindButton(string targetName)
    {
        var target = FindChild(targetName);
        return target != null ? target.GetComponent<Button>() : null;
    }

    private TextMeshProUGUI FindText(string targetName)
    {
        var target = FindChild(targetName);
        return target != null ? target.GetComponent<TextMeshProUGUI>() : null;
    }

    private Transform FindChild(string targetName)
    {
        var children = GetComponentsInChildren<Transform>(true);
        foreach (var child in children)
        {
            if (child.name == targetName)
            {
                return child;
            }
        }

        return null;
    }

    private static void SetRoot(CanvasGroup root, bool visible)
    {
        if (root == null)
        {
            return;
        }

        root.alpha = visible ? 1f : 0f;
        root.interactable = visible;
        root.blocksRaycasts = visible;
    }
}
