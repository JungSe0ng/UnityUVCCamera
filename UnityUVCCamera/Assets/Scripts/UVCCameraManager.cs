using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.UI;
using System.Linq;

public class UVCCameraManager : MonoBehaviour
{
    [System.Serializable]
    public class CameraRenderTarget
    {
        public string cameraName = "";
        public RawImage targetRawImage;
        public Texture2D cameraTexture;
        public bool isActive = false;
        public int width = 1280;
        public int height = 720;
        public int fps = 30;
        public Coroutine renderCoroutine;
    }

    AndroidJavaObject plugin;
    AndroidJavaObject activity;
    AndroidJavaClass unityPlayer;

    private bool isInitialized = false;
    private bool isFirstSetup = false;
    private bool isReconnecting = false;
    private bool wasApplicationPaused = false;

    private int maxCameras = 2;
    private List<string> availableCameras = new List<string>();
    [SerializeField] private CameraRenderTarget[] cameraTargets = new CameraRenderTarget[2];

    private bool enableAutoDetection = true;
    private bool isDetecting = false;
    private float detectionInterval = 0.5f;

    private List<string> lastKnownCameras = new List<string>();
    private bool isUSBMonitoring = false;

    public Text FrameText = null;
    public Text LeftimageText = null;
    public Text RightimageText = null;

    public Button SwapTexturesButton = null;
    public Button ConfirmButton = null;

    private Queue<(string cameraName, int cameraIndex)> closeQueue = new Queue<(string, int)>();
    private bool isProcessingCloseQueue = false;

    private float timer = 0f;
    private int frameCount = 0;

    // 게임 오브젝트 시작 시 초기화 및 모니터링 시작
    void Start()
    {
        Application.runInBackground = true;

        isFirstSetup = false;
        isInitialized = false;
        isReconnecting = false;
        wasApplicationPaused = false;

        if (cameraTargets == null || cameraTargets.Length != maxCameras)
        {
            cameraTargets = new CameraRenderTarget[maxCameras];
            for (int i = 0; i < maxCameras; i++)
            {
                cameraTargets[i] = new CameraRenderTarget();
            }
        }

        StartCoroutine(InitializeWithRetry());
        InvokeRepeating("AutoDetectAndManageCameras", 3f, detectionInterval);
        SetupButtons();
        StartCoroutine(MonitorUSBChanges());
    }

    // FPS 계산 및 UI 업데이트
    void Update()
    {
        frameCount++;
        timer += Time.unscaledDeltaTime;

        if (timer >= 0.5f)
        {
            float fps = frameCount / timer;
            if (FrameText != null)
                FrameText.text = $"Quest FPS: {fps:F0}";
            timer = 0f;
            frameCount = 0;
        }
    }

    // 게임 오브젝트 파괴 시 리소스 정리
    void OnDestroy()
    {
        isUSBMonitoring = false;
        StopAllCameras();
    }

    // 앱 일시정지 상태 처리
    void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
        {
            wasApplicationPaused = true;
        }
        else
        {
            if (wasApplicationPaused)
            {
                StartCoroutine(RecoverFromPause());
                wasApplicationPaused = false;
            }
        }
    }

    // 앱 포커스 상태 처리
    void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
        {
            wasApplicationPaused = true;
        }
        else if (wasApplicationPaused)
        {
            StartCoroutine(RecoverFromPause());
            wasApplicationPaused = false;
        }
    }

    // 앱 일시정지 후 텍스처 복원 처리
    IEnumerator RecoverFromPause()
    {
        yield return new WaitForSeconds(0.5f);

        for (int i = 0; i < cameraTargets.Length; i++)
        {
            CameraRenderTarget target = cameraTargets[i];

            if (target.isActive && target.cameraTexture != null && target.targetRawImage != null)
            {
                target.targetRawImage.texture = target.cameraTexture;

                if (target.cameraTexture.width > 0 && target.cameraTexture.height > 0)
                {
                    target.cameraTexture.Apply(false, false);
                }

                yield return new WaitForSeconds(0.1f);
            }
        }
    }

    // 버튼 이벤트 리스너 설정
    void SetupButtons()
    {
        if (SwapTexturesButton != null)
        {
            SwapTexturesButton.onClick.RemoveAllListeners();
            SwapTexturesButton.onClick.AddListener(SwapCameraTextures);
        }

        if (ConfirmButton != null)
        {
            ConfirmButton.onClick.RemoveAllListeners();
            ConfirmButton.onClick.AddListener(OnConfirmButtonClicked);
        }
    }

    // 좌우 카메라 텍스처 교체 (완전한 해결책)
    public void SwapCameraTextures()
    {
        if (cameraTargets.Length >= 2)
        {
            CameraRenderTarget leftTarget = cameraTargets[0];
            CameraRenderTarget rightTarget = cameraTargets[1];

            if (leftTarget != null && rightTarget != null &&
                leftTarget.targetRawImage != null && rightTarget.targetRawImage != null &&
                leftTarget.isActive && rightTarget.isActive)
            {
                if (leftTarget.renderCoroutine != null)
                {
                    StopCoroutine(leftTarget.renderCoroutine);
                    leftTarget.renderCoroutine = null;
                }
                if (rightTarget.renderCoroutine != null)
                {
                    StopCoroutine(rightTarget.renderCoroutine);
                    rightTarget.renderCoroutine = null;
                }

                string tempCameraName = leftTarget.cameraName;
                leftTarget.cameraName = rightTarget.cameraName;
                rightTarget.cameraName = tempCameraName;

                Texture2D tempTexture = leftTarget.cameraTexture;
                leftTarget.cameraTexture = rightTarget.cameraTexture;
                rightTarget.cameraTexture = tempTexture;

                leftTarget.targetRawImage.texture = leftTarget.cameraTexture;
                rightTarget.targetRawImage.texture = rightTarget.cameraTexture;

                leftTarget.renderCoroutine = StartCoroutine(RenderCameraFrames(0));
                rightTarget.renderCoroutine = StartCoroutine(RenderCameraFrames(1));
            }
        }
    }

    // 확인 버튼 클릭 이벤트 처리
    public void OnConfirmButtonClicked()
    {
        if (SwapTexturesButton != null)
        {
            SwapTexturesButton.gameObject.SetActive(false);
        }

        if (ConfirmButton != null)
        {
            ConfirmButton.gameObject.SetActive(false);
        }
    }

    // 2대 카메라 연결 시 버튼 활성화 확인
    void CheckAndActivateButtons()
    {
        int activeCameraCount = GetActiveCameraCount();

        if (activeCameraCount >= 2)
        {
            if (SwapTexturesButton != null && !SwapTexturesButton.gameObject.activeInHierarchy)
            {
                SwapTexturesButton.gameObject.SetActive(true);
            }

            if (ConfirmButton != null && !ConfirmButton.gameObject.activeInHierarchy)
            {
                ConfirmButton.gameObject.SetActive(true);
            }
        }
    }

    // 플러그인 초기화 및 카메라 검색 재시도
    IEnumerator InitializeWithRetry()
    {
        RequestAndroidPermissions();
        yield return new WaitForSeconds(2f);

        if (!isInitialized)
        {
            InitializePlugin();
            yield return new WaitForSeconds(1f);
            isInitialized = true;
        }

        yield return StartCoroutine(FindCamerasAndStart());
    }

    // 안드로이드 권한 요청
    void RequestAndroidPermissions()
    {
        string[] requiredPermissions = {
           "horizonos.permission.USB_CAMERA",
           Permission.Camera,
           "android.permission.CAMERA"
        };

        foreach (string permission in requiredPermissions)
        {
            if (!Permission.HasUserAuthorizedPermission(permission))
                Permission.RequestUserPermission(permission);
        }
    }

    // Java 플러그인 인스턴스 생성 및 초기화
    void InitializePlugin()
    {
        try
        {
            plugin = new AndroidJavaObject("edu.uga.engr.vel.unityuvcplugin.UnityUVCPlugin");
            unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            plugin.Call("Init", activity);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ERROR] 플러그인 초기화 실패: {e.Message}");
        }
    }

    // 연결된 카메라 검색 및 초기 설정
    IEnumerator FindCamerasAndStart()
    {
        string[] cameras = null;
        int retryCount = 0;
        const int maxRetries = 10;

        while (retryCount < maxRetries)
        {
            try
            {
                cameras = plugin.Call<string[]>("GetUSBDevices");
                if (cameras != null && cameras.Length > 0)
                {
                    availableCameras.Clear();
                    for (int i = 0; i < cameras.Length; i++)
                        availableCameras.Add(cameras[i]);
                    break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[ERROR] GetUSBDevices 실패: {e.Message}");
            }

            retryCount++;
            yield return new WaitForSeconds(1f);
        }

        if (cameras == null || cameras.Length == 0)
        {
            isFirstSetup = true;
            yield break;
        }

        int camerasToStart = Mathf.Min(cameras.Length, maxCameras);
        int successCount = 0;

        for (int i = 0; i < camerasToStart; i++)
        {
            cameraTargets[i].cameraName = cameras[i];
            bool success = false;
            yield return StartCoroutine(SetupCamera(i, (result) => success = result));

            if (success)
                successCount++;
            yield return new WaitForSeconds(1f);
        }

        CheckAndActivateButtons();
        isFirstSetup = true;
    }

    // USB 연결 상태 변화 실시간 모니터링
    IEnumerator MonitorUSBChanges()
    {
        isUSBMonitoring = true;

        while (isUSBMonitoring && isInitialized)
        {
            string[] currentCameras = null;
            List<string> currentCameraList = new List<string>();
            bool hasError = false;

            try
            {
                currentCameras = plugin.Call<string[]>("GetUSBDevices");
                currentCameraList = currentCameras?.ToList() ?? new List<string>();
            }
            catch (Exception e)
            {
                Debug.LogError($"USB 모니터링 오류: {e.Message}");
                hasError = true;
            }

            if (hasError)
            {
                yield return new WaitForSeconds(0.3f);
                continue;
            }

            List<string> newlyConnected = new List<string>();
            foreach (string camera in currentCameraList)
            {
                if (!lastKnownCameras.Contains(camera))
                {
                    newlyConnected.Add(camera);
                }
            }

            List<string> disconnected = new List<string>();
            foreach (string camera in lastKnownCameras)
            {
                if (!currentCameraList.Contains(camera))
                {
                    disconnected.Add(camera);
                }
            }

            if (newlyConnected.Count > 0 || disconnected.Count > 0)
            {
                yield return StartCoroutine(HandleUSBChanges(newlyConnected, disconnected));
            }

            lastKnownCameras = currentCameraList;

            foreach (string camera in newlyConnected)
            {
                try
                {
                    if (!HasCameraPermission(camera))
                    {
                        plugin.Call("ObtainPermission", camera);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"권한 요청 오류 {camera}: {e.Message}");
                }
            }

            yield return new WaitForSeconds(0.3f);
        }
    }

    // USB 연결 변화 처리
    IEnumerator HandleUSBChanges(List<string> newlyConnected, List<string> disconnected)
    {
        if (disconnected.Count > 0)
        {
            yield return StartCoroutine(CleanupDisconnectedCameras(lastKnownCameras));
        }

        if (newlyConnected.Count > 0)
        {
            yield return new WaitForSeconds(1f);

            List<string> updatedCameraList = new List<string>(lastKnownCameras);
            updatedCameraList.AddRange(newlyConnected);

            yield return StartCoroutine(RegisterNewCameras(updatedCameraList));
        }
    }

    // 자동 감지 시작점
    void AutoDetectAndManageCameras()
    {
        if (!enableAutoDetection || !isInitialized || isReconnecting || !isFirstSetup)
            return;

        if (!isDetecting)
            StartCoroutine(CorAutoDetectAndManageCameras());
    }

    // 카메라 자동 감지 및 관리 메인 루틴
    IEnumerator CorAutoDetectAndManageCameras()
    {
        if (isDetecting) yield break;

        isDetecting = true;

        try
        {
            string[] currentCameras = null;
            try
            {
                currentCameras = plugin.Call<string[]>("GetUSBDevices");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ERROR] GetUSBDevices 실패: {e.Message}");
                isDetecting = false;
                yield break;
            }

            int currentUSBCameraCount = currentCameras?.Length ?? 0;
            int activeRenderCameraCount = GetActiveCameraCount();

            if (currentUSBCameraCount < 2)
            {
                yield return HandleInsufficientCameras(currentCameras, currentUSBCameraCount);
            }
            else if (currentUSBCameraCount >= 2)
            {
                yield return HandleSufficientCameras(currentCameras, currentUSBCameraCount, activeRenderCameraCount);
            }
        }
        finally
        {
            isDetecting = false;
        }
    }

    // 카메라 2대 미만 상황 처리
    IEnumerator HandleInsufficientCameras(string[] currentCameras, int currentCameraCount)
    {
        List<string> currentCameraList = currentCameras?.ToList() ?? new List<string>();
        yield return CleanupDisconnectedCameras(currentCameraList);

        if (currentCameraCount > 0)
            yield return RegisterNewCameras(currentCameraList);
    }

    // 카메라 2대 이상 상황 처리
    IEnumerator HandleSufficientCameras(string[] currentCameras, int currentCameraCount, int activeCameraCount)
    {
        List<string> currentCameraList = currentCameras?.ToList() ?? new List<string>();

        if (activeCameraCount < currentCameraCount || HasDisconnectedCameras(currentCameraList))
        {
            yield return CleanupDisconnectedCameras(currentCameraList);
            yield return RegisterNewCameras(currentCameraList);
        }
    }

    // 자동 감지 기능 활성화/비활성화
    public void EnableAutoDetection(bool enable)
    {
        enableAutoDetection = enable;
    }

    // 감지 주기 설정
    public void SetDetectionInterval(float interval)
    {
        detectionInterval = Mathf.Max(0.5f, interval);
    }

    // 연결 해제된 카메라들 정리
    IEnumerator CleanupDisconnectedCameras(List<string> currentCameraList)
    {
        for (int i = 0; i < cameraTargets.Length; i++)
        {
            CameraRenderTarget target = cameraTargets[i];

            if (target.isActive && !string.IsNullOrEmpty(target.cameraName))
            {
                if (!currentCameraList.Contains(target.cameraName))
                {
                    target.isActive = false;

                    if (target.renderCoroutine != null)
                    {
                        StopCoroutine(target.renderCoroutine);
                        target.renderCoroutine = null;
                    }

                    if (target.cameraTexture != null)
                    {
                        if (target.targetRawImage != null)
                        {
                            if (target.targetRawImage.name == "RawImage_Left")
                                LeftimageText.text = "연결된 기기없음";
                            else
                                RightimageText.text = "연결된 기기없음";
                            target.targetRawImage.texture = null;
                        }

                        Texture2D tempTexture = target.cameraTexture;
                        target.cameraTexture = null;
                        StartCoroutine(DelayedTextureDestroy(tempTexture));
                    }

                    string cameraNameToClose = target.cameraName;
                    target.cameraName = "";

                    closeQueue.Enqueue((cameraNameToClose, i));

                    yield return new WaitForSeconds(0.1f);
                }
            }
        }

        if (closeQueue.Count > 0 && !isProcessingCloseQueue)
            StartCoroutine(ProcessCloseQueue());
    }

    // 새로 연결된 카메라들 등록 (수정된 버전)
    IEnumerator RegisterNewCameras(List<string> currentCameraList)
    {
        List<string> activeCameraNames = new List<string>();
        for (int i = 0; i < cameraTargets.Length; i++)
        {
            if (cameraTargets[i].isActive && !string.IsNullOrEmpty(cameraTargets[i].cameraName))
                activeCameraNames.Add(cameraTargets[i].cameraName);
        }

        List<string> newCameras = new List<string>();
        foreach (string camera in currentCameraList)
        {
            if (!activeCameraNames.Contains(camera))
                newCameras.Add(camera);
        }

        if (newCameras.Count == 0) yield break;

        int registered = 0;
        for (int i = 0; i < cameraTargets.Length && registered < newCameras.Count; i++)
        {
            CameraRenderTarget target = cameraTargets[i];

            if (!target.isActive || string.IsNullOrEmpty(target.cameraName))
            {
                string newCameraName = newCameras[registered];
                target.cameraName = newCameraName;

                bool success = false;
                yield return StartCoroutine(SetupCamera(i, (result) => success = result));

                if (success)
                    registered++;
                else
                    target.cameraName = "";

                yield return new WaitForSeconds(1f);
            }
        }

        CheckAndActivateButtons();
    }

    // 현재 활성 카메라 개수 반환
    int GetActiveCameraCount()
    {
        int count = 0;
        for (int i = 0; i < cameraTargets.Length; i++)
        {
            if (cameraTargets[i].isActive && !string.IsNullOrEmpty(cameraTargets[i].cameraName))
                count++;
        }
        return count;
    }

    // 연결 해제된 카메라 존재 여부 확인
    bool HasDisconnectedCameras(List<string> currentCameraList)
    {
        for (int i = 0; i < cameraTargets.Length; i++)
        {
            CameraRenderTarget target = cameraTargets[i];
            if (target.isActive && !string.IsNullOrEmpty(target.cameraName))
            {
                if (!currentCameraList.Contains(target.cameraName))
                    return true;
            }
        }
        return false;
    }

    // 개별 카메라 설정 및 실행
    IEnumerator SetupCamera(int cameraIndex, System.Action<bool> onComplete = null)
    {
        CameraRenderTarget target = cameraTargets[cameraIndex];
        string cameraName = target.cameraName;

        yield return StartCoroutine(RequestPermissionAndCheck(cameraName, cameraIndex));

        if (!HasCameraPermission(cameraName))
        {
            if (onComplete != null) onComplete(false);
            yield break;
        }

        bool success = false;
        yield return StartCoroutine(RunCamera(cameraIndex, (result) => success = result));

        if (onComplete != null) onComplete(success);
    }

    // 카메라 권한 요청 및 확인
    IEnumerator RequestPermissionAndCheck(string cameraName, int cameraIndex)
    {
        try
        {
            plugin.Call("ObtainPermission", cameraName);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ERROR] 카메라 {cameraIndex} 권한 요청 실패: {e.Message}");
        }

        int permissionRetries = 0;
        const int maxPermissionRetries = 6;

        while (permissionRetries < maxPermissionRetries)
        {
            try
            {
                bool hasPermission = plugin.Call<bool>("hasPermission", cameraName);
                if (hasPermission)
                    yield break;

                if (permissionRetries % 2 == 1)
                    plugin.Call("ObtainPermission", cameraName);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ERROR] 카메라 {cameraIndex} 권한 확인 실패: {e.Message}");
            }

            permissionRetries++;
            yield return new WaitForSeconds(3f);
        }
    }

    // 카메라 권한 보유 여부 확인
    bool HasCameraPermission(string cameraName)
    {
        try
        {
            return plugin.Call<bool>("hasPermission", cameraName);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ERROR] 권한 확인 실패: {e.Message}");
            return false;
        }
    }

    // 카메라 열기 및 스트림 시작
    IEnumerator RunCamera(int cameraIndex, System.Action<bool> onComplete = null)
    {
        CameraRenderTarget target = cameraTargets[cameraIndex];
        string cameraName = target.cameraName;

        try
        {
            string deviceInfo = plugin.Call<string>("GetUSBDeviceInfo", cameraName);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ERROR] 카메라 {cameraIndex} 디바이스 정보 가져오기 실패: {e.Message}");
        }

        yield return new WaitForSeconds(1f);

        string[] infos = null;
        bool openSuccess = false;
        int openRetries = 0;
        const int maxOpenRetries = 3;

        while (!openSuccess && openRetries < maxOpenRetries)
        {
            try
            {
                infos = plugin.Call<string[]>("Open", cameraName);
                openSuccess = (infos != null && infos.Length > 0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ERROR] 카메라 {cameraIndex} Open 예외: {e.Message}");
                openSuccess = false;
            }

            openRetries++;
            if (!openSuccess && openRetries < maxOpenRetries)
                yield return new WaitForSeconds(openRetries);
        }

        if (!openSuccess || infos == null)
        {
            if (onComplete != null) onComplete(false);
            yield break;
        }

        bool streamSuccess = false;
        yield return StartCoroutine(StartCameraStream(cameraIndex, (result) => streamSuccess = result));

        if (onComplete != null) onComplete(streamSuccess);
    }

    // 카메라 스트리밍 시작 및 텍스처 생성
    IEnumerator StartCameraStream(int cameraIndex, System.Action<bool> onComplete = null)
    {
        CameraRenderTarget target = cameraTargets[cameraIndex];
        string cameraName = target.cameraName;

        bool startSuccess = false;
        int width = 1280, height = 720, fps = 30;
        int format = 9;
        float bandwidth = 0.3f;

        int res = plugin.Call<int>("Start", cameraName, width, height, fps, format, bandwidth, true, false);

        if (res == 0)
        {
            startSuccess = true;
        }
        else
        {
            width = 1280; height = 720;
            res = plugin.Call<int>("Start", cameraName, width, height, fps, format, bandwidth, true, false);

            if (res == 0)
            {
                startSuccess = true;
            }
            else
            {
                width = 640; height = 480;
                res = plugin.Call<int>("Start", cameraName, width, height, fps, format, bandwidth, true, false);
                if (res == 0)
                    startSuccess = true;
            }
        }

        if (!startSuccess)
        {
            if (onComplete != null) onComplete(false);
            yield break;
        }

        if (target.cameraTexture != null)
            Destroy(target.cameraTexture);

        target.cameraTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
        target.width = width;
        target.height = height;
        target.fps = fps;
        target.isActive = true;

        if (target.targetRawImage != null)
        {
            target.targetRawImage.texture = target.cameraTexture;
        }
        else
        {
            if (onComplete != null) onComplete(false);
            yield break;
        }

        target.renderCoroutine = StartCoroutine(RenderCameraFrames(cameraIndex));
        if (onComplete != null) onComplete(true);
    }

    // 카메라 프레임 렌더링 루프
    IEnumerator RenderCameraFrames(int cameraIndex)
    {
        CameraRenderTarget target = cameraTargets[cameraIndex];
        string cameraName = target.cameraName;

        int frameCount = 0;
        int errorCount = 0;
        const int maxErrors = 10;

        while (target.isActive && errorCount < maxErrors)
        {
            sbyte[] frameData = null;
            bool frameSuccess = false;

            string[] currentCameras = plugin.Call<string[]>("GetUSBDevices");
            bool isConnected = false;

            foreach (string cam in currentCameras)
            {
                if (cam == cameraName)
                {
                    isConnected = true;
                    break;
                }
            }

            if (!isConnected)
            {
                if (target.targetRawImage != null)
                    target.targetRawImage.texture = null;
                if (target.targetRawImage.name == "RawImage_Left")
                    LeftimageText.text = "연결된 기기 없음";
                else
                    RightimageText.text = "연결된 기기 없음";
                OnConfirmButtonClicked();
                break;
            }

            var frameReceivedTime = Time.realtimeSinceStartup;
            try
            {
                frameData = plugin.Call<sbyte[]>("GetFrameData", cameraName);
                frameSuccess = true;
            }
            catch (Exception e)
            {
                errorCount++;
                Debug.LogError($"[ERROR] 카메라 {cameraIndex} 프레임 데이터 에러 ({errorCount}/{maxErrors}): {e.Message}");
                frameSuccess = false;
            }

            if (frameSuccess && frameData != null && frameData.Length > 0)
            {
                try
                {
                    target.cameraTexture.LoadRawTextureData((byte[])(System.Array)frameData);
                    target.cameraTexture.Apply(false, false);
                    var renderCompleteTime = Time.realtimeSinceStartup;

                    if (frameCount % 10 == 0)
                    {
                        if (cameraTargets[cameraIndex].targetRawImage != null)
                        {
                            if (LeftimageText != null && cameraTargets[cameraIndex].targetRawImage.name == "RawImage_Left")
                                LeftimageText.text = $"왼쪽 카메라 프레임 전송\n시간: {(renderCompleteTime - frameReceivedTime) * 1000f:F1} ms \n{cameraTargets[cameraIndex].cameraName}";
                            else if (RightimageText != null)
                                RightimageText.text = $"오른쪽 카메라 프레임 전송 시간: {(renderCompleteTime - frameReceivedTime) * 1000f:F1} ms \n{cameraTargets[cameraIndex].cameraName}";
                        }
                    }
                    frameCount++;
                    errorCount = 0;
                }
                catch (Exception e)
                {
                    errorCount++;
                    Debug.LogError($"[ERROR] 카메라 {cameraIndex} 텍스처 업데이트 에러 ({errorCount}/{maxErrors}): {e.Message}");
                }
            }
            else if (frameSuccess)
            {
                errorCount++;
            }

            if (!frameSuccess || (frameData == null || frameData.Length == 0))
            {
                if (errorCount >= maxErrors)
                    break;
                yield return new WaitForSeconds(0.1f);
            }
            else
            {
                yield return null;
            }
        }

        target.isActive = false;
    }

    // Close 요청 큐 순차 처리
    IEnumerator ProcessCloseQueue()
    {
        if (isProcessingCloseQueue) yield break;

        isProcessingCloseQueue = true;

        while (closeQueue.Count > 0)
        {
            var (cameraName, cameraIndex) = closeQueue.Dequeue();
            yield return new WaitForSeconds(0.3f);

            try
            {
                int closeResult = plugin.Call<int>("Close", cameraName);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ERROR] 카메라 {cameraIndex} 닫기 실패: {e.Message}");
            }

            yield return new WaitForSeconds(0.2f);
        }

        isProcessingCloseQueue = false;
    }

    // 종료 시 Close 큐 처리
    IEnumerator ProcessCloseQueueOnDestroy()
    {
        while (closeQueue.Count > 0)
        {
            var (cameraName, cameraIndex) = closeQueue.Dequeue();

            try
            {
                int r = plugin.Call<int>("Close", cameraName);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ERROR] 종료 시 카메라 {cameraIndex} Close 실패: {e.Message}");
            }

            yield return new WaitForSeconds(0.1f);
        }
    }

    // 텍스처 안전 삭제
    IEnumerator DelayedTextureDestroy(Texture2D texture)
    {
        yield return null;
        if (texture != null)
            Destroy(texture);
    }

    // 모든 카메라 정리 및 리소스 해제
    void StopAllCameras()
    {
        for (int i = 0; i < cameraTargets.Length; i++)
        {
            CameraRenderTarget target = cameraTargets[i];
            if (target != null)
            {
                target.isActive = false;

                if (target.renderCoroutine != null)
                {
                    StopCoroutine(target.renderCoroutine);
                    target.renderCoroutine = null;
                }

                if (target.cameraTexture != null)
                {
                    if (target.targetRawImage != null)
                        target.targetRawImage.texture = null;
                    Destroy(target.cameraTexture);
                    target.cameraTexture = null;
                }

                if (plugin != null && !string.IsNullOrEmpty(target.cameraName))
                {
                    closeQueue.Enqueue((target.cameraName, i));
                    target.cameraName = "";
                }

                target.width = 1280;
                target.height = 720;
                target.fps = 30;
            }
        }

        if (closeQueue.Count > 0)
            StartCoroutine(ProcessCloseQueueOnDestroy());
    }
}