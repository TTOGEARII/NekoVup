# NekoVup

영상의 **워터마크를 지우고 업스케일**하는 Windows 프로그램.

직접 영상을 처리하지 않고, 설치된 도구들을 순서대로 불러 쓰는 지휘자 역할을 합니다.

- [FFmpeg](https://ffmpeg.org/) — 프레임 추출·합성·인코딩
- [ProPainter](https://github.com/sczhou/ProPainter) — 워터마크 자리 복원 (Python + PyTorch, NVIDIA GPU)
- [Video2X](https://github.com/k4yt3x/video2x) — 업스케일 (RealESRGAN / RealCUGAN)

창 화면 판(`NekoVup.exe`)과 명령줄 판(`nekovup.exe`)이 있습니다.

## 설치

[Releases](https://github.com/TTOGEARII/NekoVup/releases/latest)에서 **`NekoVup-win-Setup.exe`** 를 받아 실행하면 끝입니다.
관리자 권한 없이 사용자 폴더(`%LocalAppData%\NekoVup`)에 설치되고, 바탕 화면과 시작 메뉴에 바로 가기가 생깁니다.

- **업데이트**: 새 버전이 나오면 프로그램 아래 줄에 업데이트 버튼이 뜹니다. 누르면 내려받아 적용하고 다시 시작합니다.
- **제거**: Windows 설정의 앱 목록, 또는 프로그램의 설정 → 정보 → 프로그램 제거.
- 설치하지 않고 쓰려면 `NekoVup-win-Portable.zip`을 풀어서 실행하세요 (자동 업데이트는 되지 않습니다).
- 명령줄 판은 `nekovup-cli-win-x64.zip`에 들어 있습니다.

설정은 `%AppData%\NekoVup\settings.json`에 저장되며 프로그램을 지워도 남습니다.

## 할 수 있는 일

| 워터마크 제거 | 설명 |
|---|---|
| ProPainter | 워터마크 둘레만 잘라 복원한 뒤 원본 위에 얹습니다. 느리지만 자연스럽습니다. |
| delogo | FFmpeg의 delogo 필터. 아주 빠르지만 뭉갠 자국이 남습니다. |
| 끔 | 업스케일만 합니다. |

| 업스케일 | 모델 | 배율 |
|---|---|---|
| 애니메이션 — 빠름 | RealESRGAN realesr-animevideov3 | 2 · 3 · 4배 |
| 애니메이션 — 고품질 | RealCUGAN models-se | 2 · 3 · 4배 |
| 실사 | RealESRGAN realesrgan-plus | 2 · 3 · 4배 (4배로 키운 뒤 줄임) |

- 복원한 부분만 원본에 얹기 때문에 워터마크 바깥 화면은 원본 그대로입니다.
- 오디오는 그대로 옮기고, `.mkv`로 저장하면 자막과 글꼴 첨부도 유지됩니다.
- 중간에 멈추거나 실패해도 같은 설정으로 다시 시작하면 끝난 부분은 건너뛰고 이어서 합니다.

**제약**: 워터마크 위치가 영상 내내 같아야 합니다(고정된 로고). 움직이는 워터마크는 지원하지 않습니다.

## 필요한 도구

도구는 프로그램에 들어 있지 않습니다. 설치한 뒤 **설정 → 도구**에서 위치를 알려 주고 **환경 점검**으로 확인하세요.

- Windows 10/11 64비트
- FFmpeg: `winget install Gyan.FFmpeg`
- ProPainter로 복원하려면
  - NVIDIA 그래픽카드 (VRAM 8GB 권장)
  - Python 3.11
  - ProPainter 저장소와 그 안의 가상 환경:
    ```powershell
    git clone https://github.com/sczhou/ProPainter
    cd ProPainter
    py -3.11 -m venv venv
    venv\Scripts\python -m pip install torch torchvision --index-url https://download.pytorch.org/whl/cu121
    venv\Scripts\python -m pip install -r requirements.txt
    ```
  - 첫 실행 때 모델 가중치를 자동으로 내려받습니다(인터넷 필요).
- 업스케일하려면 [Video2X 6.4.0](https://github.com/k4yt3x/video2x/releases/tag/6.4.0) Windows 판 (압축만 풀면 됩니다)

## 사용법 — 창 화면

1. 영상을 창에 끌어다 놓거나 **파일 고르기**.
2. 위쪽 카드에서 할 일을 켭니다: **워터마크 제거**, **업스케일**.
3. 미리보기 위에서 워터마크를 끌어서 박스로 감쌉니다. 박스 안을 끌면 이동, 손잡이로 크기 조절, Delete로 삭제. 숫자로 직접 적어도 됩니다.
   노란 점선은 실제로 복원 처리되는 영역입니다.
4. 처음에는 **앞부분만 시험 삼아 처리**로 결과를 확인해 보세요.
5. **시작**. 끝나면 타임라인 옆 스위치로 원본과 결과를 번갈아 볼 수 있습니다.

화면은 화이트(종이)와 다크(먹) 두 가지 테마가 있고, 아래 줄의 버튼이나 설정 → 화면에서 바꿉니다.

옵션을 붙여 실행할 수도 있습니다: `NekoVup.exe 영상.mp4 --box 1040,40,190,56 --upscale anime --scale 2`

## 사용법 — 명령줄

```powershell
nekovup check                                    # 도구 설치 상태 점검
nekovup settings --python D:\ProPainter\venv\Scripts\python.exe --propainter D:\ProPainter --video2x D:\video2x\video2x.exe

nekovup probe 영상.mp4                            # 영상 정보
nekovup plan  영상.mp4 --box 1040,40,190,56       # 처리 영역·필요 공간 미리 보기

nekovup run 영상.mp4 -o 결과.mkv --box 1040,40,190,56 --test-seconds 5      # 앞 5초만 시험
nekovup run 영상.mp4 -o 결과.mkv --box 1040,40,190,56                       # 워터마크 제거
nekovup run 영상.mp4 -o 결과.mkv --box 1040,40,190,56 --upscale anime       # 제거 + 2배 업스케일
nekovup run 영상.mp4 -o 결과.mkv --upscale anime-hq --scale 3               # 업스케일만
nekovup run 영상.mp4 -o 결과.mp4 --box 1040,40,190,56 --mode delogo         # 빠른 모드
```

- `--box x,y,w,h` — 워터마크 위치(픽셀). 여러 개면 여러 번 적습니다.
- 중간 파일은 출력 폴더의 `.nekovup`에 생겼다가 성공하면 지워집니다.
- 전체 옵션: `nekovup --help`

## 빌드

```powershell
dotnet build
dotnet test --project tests\NekoVup.Tests     # ffmpeg, python(numpy·Pillow)이 있으면 전체 과정까지 검증
.\tools\publish-cli.ps1                        # publish\cli\nekovup.exe
.\tools\publish-app.ps1                        # publish\app\NekoVup.exe (폴더째 복사해서 실행)
.\tools\pack.ps1                               # Releases\NekoVup-win-Setup.exe (설치 파일)
```

- .NET 10 SDK가 필요하고, 게시(Native AOT)에는 Visual Studio(또는 Build Tools)의 "C++를 사용한 데스크톱 개발"과 Windows SDK가 필요합니다.
- 설치 파일을 만들려면 Velopack 도구도 필요합니다: `dotnet tool install -g vpk --version 1.2.161`
- 아이콘과 설치 화면 그림은 `powershell -Sta -File tools\gen-icon.ps1`로 다시 만듭니다.

### 배포

1. `src\NekoVup.App\NekoVup.App.csproj`의 `<Version>`을 올립니다.
2. 커밋하고 `main`에 푸시한 뒤, 같은 번호의 태그를 푸시합니다: `git tag v1.0.1 && git push origin v1.0.1`
3. 태그가 올라가면 Release 워크플로가 설치 파일을 빌드해 GitHub Release에 올립니다. 설치된 프로그램은 다음에 켤 때 새 버전을 알아챕니다.

## 구조

```
src/NekoVup.Core    처리 과정 전체 (화면과 무관한 라이브러리)
src/NekoVup.Cli     명령줄 판
src/NekoVup.App     창 화면 판 (WinUI 3)
tests/NekoVup.Tests 단위·통합 테스트
tools/              게시·설치 파일·아이콘 스크립트
tools/mock          테스트용 가짜 ProPainter·Video2X (GPU 없이 전체 과정을 검증)
assets/             설치 화면 그림
```

## 라이선스 참고

이 프로그램이 불러 쓰는 도구는 각자의 라이선스를 따릅니다.
ProPainter는 비상업적 용도만 허용하고(S-Lab License 1.0), Video2X는 AGPL-3.0입니다.
