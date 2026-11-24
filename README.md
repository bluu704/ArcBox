# ArcBox

Windows용 압축 프로그램입니다. 7-Zip 엔진(7z.dll)을 사용합니다.

## 설치
- `dist/ArcBox-Setup-1.3.0.exe`를 실행합니다. 관리자 권한 없이 사용자 폴더(`%LOCALAPPDATA%\Programs\ArcBox`)에 설치됩니다.
- 요구 사항: 64비트 Windows 10/11, .NET Framework 4.8 (Windows 10 1903 이상에는 기본 포함)
- 포터블: `dist/ArcBox-1.3.0-portable.zip`의 압축을 풀고 `ArcBox.exe`를 실행합니다. 처음 실행할 때 파일 연결과 메뉴가 현재 사용자에게 등록됩니다.

## 기능
- 열기/풀기: **ALZ, EGG, ACE, ARC, ZOO, PMA, ZPAQ, HA, PEA, LZ, LZ4, BR, UUE/XXE, AES Crypt**(알집 — 암호 ZipCrypto/AES/LEA, 분할, 솔리드, AZO/LZMA/BZip2 포함), ZIP, 7Z, RAR(4/5), TAR, GZ, BZ2, XZ, ZST, CAB, ISO, ARJ, LZH, WIM, CPIO, RPM, DEB, DMG, VHD 등 (7-Zip 지원 형식 전체)
- 압축: ZIP, 7Z, TAR, TAR.GZ, TAR.BZ2, TAR.XZ, GZ, BZ2, XZ
- 암호(AES-256, ZipCrypto), 7Z 파일 이름 암호화, 분할 압축(.001), 분할 파일 열기
- 압축 파일 안에서 추가, 삭제, 이름 바꾸기, 새 폴더 만들기. 수정은 임시 파일에 먼저 쓴 뒤 교체하므로 실패해도 원본이 유지됩니다
- 스마트 풀기(안에 폴더가 하나뿐이면 그대로 풀기), 덮어쓰기 묻기/건너뛰기/이름 바꾸기
- 압축을 풀지 않고 이미지 미리보기(JPG, PNG, GIF, BMP, TIFF, ICO, WebP·HEIC·AVIF는 Windows 코덱 사용), ←/→로 넘기기
- 압축 파일 테스트, 정보 보기, 해시(CRC32/MD5/SHA-1/SHA-256)
- 탐색기 우클릭 메뉴(여러 파일을 선택해도 하나로 처리), 파일 아이콘과 연결
- 끌어 놓기로 열기/압축/추가, 끌어 내기로 꺼내기, 안의 파일을 열어 수정하면 다시 반영할지 묻기
- 파일 이름 인코딩 변경(CP949, UTF-8, Shift-JIS 등), 라이트/다크 테마
- 경로 탈출(zip-slip) 차단, 예약된 이름(CON 등) 처리, 긴 경로 지원

## 명령줄
`ArcBox.exe [압축파일]`, `/x` 여기에 풀기(스마트), `/xf` 폴더로 풀기, `/xd` 풀기 창, `/t` 테스트,
`/a` 압축 창, `/az` ZIP으로 바로 압축, `/a7` 7Z로 바로 압축, `/hash` 해시, `/register`, `/unregister`, `/settings`

## 알려진 제한
- ALZ, EGG, ACE, ARC, ZOO, PMA, ZPAQ, HA, PEA, LZ, LZ4, BR, UU/UUE/XXE, AES는 열기·풀기·테스트만 됩니다(수정·새로 만들기는 지원하지 않음)
- 암호화된 ACE, 분할 ACE, 암호화된 ZPAQ, 암호화된 PEA, ARC의 옛 Crunch(5~7)/Crushed/Distilled 방식은 아직 지원하지 않습니다
- BH, SQX는 공개된 명세나 해제 코드가 없는 독점 형식이라 열 수 없습니다(아이콘과 연결만 됨)
- HA, PEA는 원본 압축 코드를 옮긴 압축기로 만든 파일로만 검증했습니다(실제 HA/PeaZip 프로그램으로 만든 샘플로는 아직 확인하지 못함)
- Windows 11에서는 우클릭 메뉴가 "더 많은 옵션 표시" 안에 나타납니다
- 코드 서명이 없어서 처음 실행할 때 SmartScreen 경고가 나올 수 있습니다
- Windows에서 사용자가 직접 다른 프로그램을 기본값으로 고른 형식은 "설정 > 파일 연결 > Windows 기본 앱 설정 열기"에서 바꿔야 합니다

## 빌드 (Linux)
`./build.sh` — .NET SDK 8과 NSIS(makensis)가 필요합니다. 결과물은 `dist/`에 생깁니다.
엔진 자체 테스트: `ArcBox.exe /selftest <작업폴더> <로그파일>`, ALZ/EGG 샘플 테스트: `ArcBox.exe /selftest-ko <샘플폴더> <출력폴더> <로그파일>`

## 라이선스
ArcBox.exe에는 GPL인 HA 해제 코드가 들어 있어서 **GNU GPL v2 이상**으로 배포합니다(`COPYING.txt`, 전체 소스는 `ArcBox-source.zip`).
7z.dll은 7-Zip(Igor Pavlov)의 GNU LGPL + unRAR 제한 라이선스를 따릅니다(`7-Zip-License.txt`).
ALZ/EGG/AZO 해제 코드는 MIT 라이선스 Rust 구현(unalz, unegg, libazo)을 C#으로 옮긴 것입니다. 그 밖의 출처는 `THIRD-PARTY-NOTICES.txt`에 있습니다.
