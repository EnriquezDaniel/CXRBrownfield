using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

/// <summary>
/// Simple test setup for ModelRequester functionality
/// This script helps you quickly test all phases without manual UI setup
/// </summary>
public class QuickTester : MonoBehaviour
{
    [Header("Quick Test Options")]
    public bool autoCreateUI = true;
    public bool useAutoLayout = true; // If false, uses manual positioning
    
    [Header("Layout Settings")]
    public Vector2 containerMargins = new Vector2(20, 20);
    public float elementSpacing = 10f;
    public int paddingLeft = 15;
    public int paddingRight = 15;
    public int paddingTop = 15;
    public int paddingBottom = 15;
    
    [Header("Manual Positioning (when useAutoLayout = false)")]
    public Vector2 titlePosition = new Vector2(50, -50);
    public Vector2 subtitlePosition = new Vector2(50, -90);
    public Vector2 healthButtonPosition = new Vector2(50, -130);
    public Vector2 modelsButtonPosition = new Vector2(50, -180);
    public Vector2 searchLabelPosition = new Vector2(50, -230);
    public Vector2 searchInputPosition = new Vector2(50, -270);
    public Vector2 searchButtonPosition = new Vector2(50, -320);
    public Vector2 downloadLabelPosition = new Vector2(50, -370);
    public Vector2 downloadButtonPosition = new Vector2(50, -410);
    public Vector2 statusInfoPosition = new Vector2(50, -460);
    public Vector2 pathInfoPosition = new Vector2(50, -500);
    
    [Header("Element Sizes")]
    public Vector2 buttonSize = new Vector2(200, 40);
    public Vector2 inputSize = new Vector2(300, 35);
    public Vector2 textSize = new Vector2(400, 30);
    
    [Header("Fonts and Colors")]
    public int titleFontSize = 24;
    public int subtitleFontSize = 14;
    public int buttonFontSize = 16;
    public int labelFontSize = 18;
    public int infoFontSize = 12;
    public Color textColor = Color.white;
    public Color buttonColor = new Color(0.2f, 0.6f, 1f, 0.8f);
    
    private ModelRequester modelRequester;
    private Canvas canvas;
    private GameObject uiContainer;
    
    private void Start()
    {
        // Find or create ModelRequester
        modelRequester = FindFirstObjectByType<ModelRequester>();
        if (modelRequester == null)
        {
            GameObject requesterGO = new GameObject("ModelRequester");
            modelRequester = requesterGO.AddComponent<ModelRequester>();
            Debug.Log("[QuickTester] Created ModelRequester automatically");
        }
        
        if (autoCreateUI)
        {
            CreateQuickTestUI();
        }
    }
    
    private void CreateQuickTestUI()
    {
        // Ensure EventSystem exists (required for UI interaction)
        if (EventSystem.current == null)
        {
            GameObject eventSystemGO = new GameObject("EventSystem");
            eventSystemGO.AddComponent<EventSystem>();
            eventSystemGO.AddComponent<StandaloneInputModule>();
            Debug.Log("[QuickTester] Created EventSystem for UI interaction");
        }
        
        // Create Canvas
        GameObject canvasGO = new GameObject("TestCanvas");
        canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100; // Ensure it's on top
        
        CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        
        canvasGO.AddComponent<GraphicRaycaster>();
        
        Debug.Log($"[QuickTester] Created Canvas - Screen size: {Screen.width}x{Screen.height}");
        
        // Create UI container
        uiContainer = new GameObject("UIContainer");
        uiContainer.transform.SetParent(canvas.transform, false);
        RectTransform containerRect = uiContainer.AddComponent<RectTransform>();
        
        if (useAutoLayout)
        {
            // Full screen container for auto layout
            containerRect.anchorMin = Vector2.zero;
            containerRect.anchorMax = Vector2.one;
            containerRect.offsetMin = new Vector2(containerMargins.x, containerMargins.y);
            containerRect.offsetMax = new Vector2(-containerMargins.x, -containerMargins.y);
            
            VerticalLayoutGroup layout = uiContainer.AddComponent<VerticalLayoutGroup>();
            layout.spacing = elementSpacing;
            layout.padding = new RectOffset(paddingLeft, paddingRight, paddingTop, paddingBottom);
            layout.childControlHeight = false;
            layout.childControlWidth = false;
            layout.childForceExpandWidth = false;
            
            Debug.Log("[QuickTester] Using automatic vertical layout");
        }
        else
        {
            // Manual positioning - use full screen as reference
            containerRect.anchorMin = Vector2.zero;
            containerRect.anchorMax = Vector2.one;
            containerRect.offsetMin = Vector2.zero;
            containerRect.offsetMax = Vector2.zero;
            
            Debug.Log("[QuickTester] Using manual positioning");
        }
        
        // Create UI elements with visible colors for debugging
        CreateText("Model Requester - Quick Test", titleFontSize, useAutoLayout ? Vector2.zero : titlePosition);
        CreateText("Make sure server is running on http://localhost:5002", subtitleFontSize, useAutoLayout ? Vector2.zero : subtitlePosition);
        
        CreateButton("Health Check", () => modelRequester.TestServerConnectionButton(), useAutoLayout ? Vector2.zero : healthButtonPosition);
        CreateButton("Get All Models", () => modelRequester.GetModelList(), useAutoLayout ? Vector2.zero : modelsButtonPosition);
        
        CreateText("Search Test:", labelFontSize, useAutoLayout ? Vector2.zero : searchLabelPosition);
        var searchInput = CreateInputField("Enter search query (e.g., 'cat')", useAutoLayout ? Vector2.zero : searchInputPosition);
        CreateButton("Search Models", () => {
            string query = searchInput.text.Trim();
            if (string.IsNullOrEmpty(query)) query = "cat";
            modelRequester.SearchModels(query);
        }, useAutoLayout ? Vector2.zero : searchButtonPosition);
        
        CreateText("Download Test (searches for 'cat' and downloads if found):", labelFontSize, useAutoLayout ? Vector2.zero : downloadLabelPosition);
        CreateButton("Test Auto-Download", () => {
            modelRequester.SearchModels("cat"); // Will auto-download if single result
        }, useAutoLayout ? Vector2.zero : downloadButtonPosition);
        
        CreateText("Check the Console window for detailed logs!", infoFontSize, useAutoLayout ? Vector2.zero : statusInfoPosition);
        CreateText($"Downloaded files saved to: {Application.persistentDataPath}/models/", infoFontSize, useAutoLayout ? Vector2.zero : pathInfoPosition);
        
        Debug.Log("[QuickTester] Created test UI - check your Game view!");
        Debug.Log($"[QuickTester] Using {(useAutoLayout ? "automatic" : "manual")} layout positioning");
        Debug.Log($"[QuickTester] Canvas created with sortingOrder: {canvas.sortingOrder}");
    }
    
    private Text CreateText(string content, int fontSize, Vector2 position = default)
    {
        GameObject textGO = new GameObject($"Text_{content.Substring(0, Mathf.Min(10, content.Length))}");
        textGO.transform.SetParent(uiContainer.transform, false);
        
        Text text = textGO.AddComponent<Text>();
        text.text = content;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = fontSize;
        text.color = textColor;
        text.raycastTarget = false; // Improve performance
        
        RectTransform rect = textGO.GetComponent<RectTransform>();
        
        if (useAutoLayout)
        {
            rect.sizeDelta = new Vector2(textSize.x, fontSize + 10);
        }
        else
        {
            // Manual positioning - anchor to top-left
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(textSize.x, fontSize + 10);
            
            Debug.Log($"[QuickTester] Created text '{content.Substring(0, Mathf.Min(20, content.Length))}...' at position {position}");
        }
        
        return text;
    }
    
    private Button CreateButton(string label, System.Action onClick, Vector2 position = default)
    {
        GameObject buttonGO = new GameObject($"Button_{label}");
        buttonGO.transform.SetParent(uiContainer.transform, false);
        
        Image buttonImage = buttonGO.AddComponent<Image>();
        buttonImage.color = buttonColor;
        
        Button button = buttonGO.AddComponent<Button>();
        button.targetGraphic = buttonImage;
        button.onClick.AddListener(() => {
            Debug.Log($"[QuickTester] Button clicked: {label}");
            onClick?.Invoke();
        });
        
        RectTransform buttonRect = buttonGO.GetComponent<RectTransform>();
        
        if (useAutoLayout)
        {
            buttonRect.sizeDelta = buttonSize;
        }
        else
        {
            // Manual positioning - anchor to top-left
            buttonRect.anchorMin = new Vector2(0, 1);
            buttonRect.anchorMax = new Vector2(0, 1);
            buttonRect.pivot = new Vector2(0, 1);
            buttonRect.anchoredPosition = position;
            buttonRect.sizeDelta = buttonSize;
            
            Debug.Log($"[QuickTester] Created button '{label}' at position {position} with size {buttonSize}");
        }
        
        // Add button text
        GameObject textGO = new GameObject("Text");
        textGO.transform.SetParent(buttonGO.transform, false);
        
        Text text = textGO.AddComponent<Text>();
        text.text = label;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = buttonFontSize;
        text.color = textColor;
        text.alignment = TextAnchor.MiddleCenter;
        text.raycastTarget = false;
        
        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        
        return button;
    }
    
    private InputField CreateInputField(string placeholder, Vector2 position = default)
    {
        GameObject inputGO = new GameObject("InputField");
        inputGO.transform.SetParent(uiContainer.transform, false);
        
        Image inputImage = inputGO.AddComponent<Image>();
        inputImage.color = new Color(0.2f, 0.2f, 0.2f, 0.8f); // Darker background for visibility
        
        InputField inputField = inputGO.AddComponent<InputField>();
        
        RectTransform inputRect = inputGO.GetComponent<RectTransform>();
        
        if (useAutoLayout)
        {
            inputRect.sizeDelta = inputSize;
        }
        else
        {
            // Manual positioning - anchor to top-left
            inputRect.anchorMin = new Vector2(0, 1);
            inputRect.anchorMax = new Vector2(0, 1);
            inputRect.pivot = new Vector2(0, 1);
            inputRect.anchoredPosition = position;
            inputRect.sizeDelta = inputSize;
            
            Debug.Log($"[QuickTester] Created input field at position {position} with size {inputSize}");
        }
        
        // Create text component
        GameObject textGO = new GameObject("Text");
        textGO.transform.SetParent(inputGO.transform, false);
        
        Text text = textGO.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 14;
        text.color = textColor;
        text.supportRichText = false;
        
        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(10, 0);
        textRect.offsetMax = new Vector2(-10, 0);
        
        inputField.textComponent = text;
        
        // Create placeholder
        GameObject placeholderGO = new GameObject("Placeholder");
        placeholderGO.transform.SetParent(inputGO.transform, false);
        Text placeholderText = placeholderGO.AddComponent<Text>();
        placeholderText.text = placeholder;
        placeholderText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        placeholderText.fontSize = 14;
        placeholderText.color = new Color(0.7f, 0.7f, 0.7f, 0.8f);
        placeholderText.fontStyle = FontStyle.Italic;
        
        RectTransform placeholderRect = placeholderGO.GetComponent<RectTransform>();
        placeholderRect.anchorMin = Vector2.zero;
        placeholderRect.anchorMax = Vector2.one;
        placeholderRect.offsetMin = new Vector2(10, 0);
        placeholderRect.offsetMax = new Vector2(-10, 0);
        
        inputField.placeholder = placeholderText;
        
        return inputField;
    }
}