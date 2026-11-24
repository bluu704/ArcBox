# ArcBox

Windows용 압축 프로그램입니다. 7-Zip 엔진(7z.dll)을 사용합니다.

## 다운로드
**[ArcBox 1.0.0 받기](https://github.com/bluu704/ArcBox/releases/latest)**

받은 zip의 압축을 풀고 `ArcBox-Setup-1.0.0.exe`를 실행하세요. 관리자 권한은 필요 없습니다.

- 64비트 Windows 10/11
- 코드 서명이 없어서 처음 실행할 때 SmartScreen 경고가 나올 수 있습니다. "추가 정보 → 실행"을 누르세요.

## 기능
- 열기: ZIP, 7Z, RAR, TAR, GZ, ISO 등 7-Zip이 여는 형식 전체 + ALZ, EGG, ACE, ARC, ZOO, ZPAQ, HA, PEA 등
- 압축: ZIP, 7Z, TAR(GZ/BZ2/XZ), GZ, BZ2, XZ — 암호, 분할 압축 지원
- 압축 파일 안에서 추가·삭제·이름 바꾸기
- 스마트 풀기, 이미지 미리보기, 해시 확인
- 더블클릭 동작 선택: 열기 · 여기에 풀기 · 폴더로 풀기 · 메뉴 보이기 (설정 → 파일 연결)
- 보기 방식(자세히·아이콘·썸네일), 미리보기 창, 뒤로/앞으로 이동
- 이미지 뷰어(회전·슬라이드쇼·전체 화면·썸네일 줄), 텍스트 파일 바로 보기
- 탐색기 우클릭 메뉴, 끌어 놓기, 다크 테마

ALZ, EGG 등 7-Zip 밖의 형식은 열기·풀기만 됩니다.

## 빌드
Linux에서 `./build.sh` (.NET SDK 8, NSIS 필요). 결과물은 `dist/`에 생깁니다.

## 라이선스
ArcBox는 **GNU GPL v2 이상**입니다(`COPYING.txt`). 7z.dll은 7-Zip 라이선스(`7-Zip-License.txt`)를 따릅니다. 그 밖의 출처는 `THIRD-PARTY-NOTICES.txt`를 보세요.
