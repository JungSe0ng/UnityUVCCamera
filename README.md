# UnityUVCCamera

Meta Quest에서 USB 웹캠 2대를 연결하여 사용하기 위한 Unity Android 프로젝트입니다. [velaboratory/UnityUVCPlugin](https://github.com/velaboratory/UnityUVCPlugin)을 기반으로 Quest 환경에 최적화했습니다.

## 주요 기능

- **Meta Quest 웹캠 지원**: USB 웹캠 2대를 Quest에 연결하여 동시 스트리밍
- **자동 권한 관리**: USB 장치 연결 시 자동 권한 처리 (HorizonOS 지원)
- **실시간 모니터링**: 웹캠 연결/해제 자동 감지 및 복구
- **고성능 렌더링**: 1280x720@30fps MJPEG 스트리밍

## 프로젝트 세팅

### 빌드 설정
- **플랫폼**: Android
- **Graphics APIs**: OpenGLES3
- **Minimum API Level**: Android 12.0
- **Scripting Backend**: IL2CPP
- **Target Architectures**: ARM64

### 매니페스트 권한
```xml
<uses-permission android:name="android.permission.CAMERA" />
<uses-permission android:name="horizonos.permission.USB_CAMERA" />
<uses-feature android:name="android.hardware.usb.host" />
```

## 사용법

### 1. 하드웨어 연결
USB-C 허브를 통해 Meta Quest에 USB 웹캠 2대 연결

### 2. Unity 씬 설정
1. GameObject 생성 → 이름을 **반드시 "UVCCameraManager"로 설정**
2. 해당 GameObject에 `UVCCameraManager` 스크립트 추가
3. UI Canvas에 웹캠 화면용 RawImage 2개 생성
4. UVCCameraManager 인스펙터에서:
   - `Camera Targets[0]` → `Target Raw Image`에 왼쪽 RawImage 연결
   - `Camera Targets[1]` → `Target Raw Image`에 오른쪽 RawImage 연결

### 3. 자동 동작
- 앱 실행 시 웹캠 자동 검색
- 웹캠 연결 시 자동 권한 요청
- 2대 카메라 동시 스트리밍 시작
- 좌우 카메라 교체 버튼 기능 (`SwapTexturesButton`)
