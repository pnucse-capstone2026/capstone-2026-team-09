using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VRoom.Backend
{
    /// <summary>
    /// Windows 네이티브 파일 선택 창(comdlg32.dll GetOpenFileName)을 여는 래퍼.
    ///
    /// Unity 에는 런타임 파일 다이얼로그가 없어 P/Invoke 로 직접 부른다.
    /// Windows 외 플랫폼에서는 경고만 남기고 null 을 돌려준다.
    /// </summary>
    public static class WindowsFileDialog
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        /// <summary>
        /// Win32 OPENFILENAME 구조체. 필드 순서와 개수가 네이티브 정의와 정확히
        /// 일치해야 하므로 임의로 추가·삭제·재배열하면 안 된다.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class OpenFileName
        {
            public int structSize = 0;
            public IntPtr dlgOwner = IntPtr.Zero;
            public IntPtr instance = IntPtr.Zero;
            public string filter = null;
            public string customFilter = null;
            public int maxCustFilter = 0;
            public int filterIndex = 0;
            public string file = null;
            public int maxFile = 0;
            public string fileTitle = null;
            public int maxFileTitle = 0;
            public string initialDir = null;
            public string title = null;
            public int flags = 0;
            public short fileOffset = 0;
            public short fileExtension = 0;
            public string defExt = null;
            public IntPtr custData = IntPtr.Zero;
            public IntPtr hook = IntPtr.Zero;
            public string templateName = null;
            public IntPtr reservedPtr = IntPtr.Zero;
            public int reservedInt = 0;
            public int flagsEx = 0;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

        /// <summary>파일 선택 창을 연다. 취소하면 null 반환.</summary>
        public static string Open(string title = "면접 정보 txt 선택")
        {
            var ofn = new OpenFileName();
            ofn.structSize = Marshal.SizeOf(ofn);
            // "표시명\0*.txt\0" 형태. \0\0 로 종료
            ofn.filter = "텍스트 파일 (*.txt)\0*.txt\0모든 파일 (*.*)\0*.*\0\0";
            // 결과를 받아올 버퍼. 네이티브가 여기에 경로를 써 넣으므로 미리 크기를 잡아 둔다.
            ofn.file = new string(new char[1024]);
            ofn.maxFile = ofn.file.Length;
            ofn.fileTitle = new string(new char[256]);
            ofn.maxFileTitle = ofn.fileTitle.Length;
            ofn.title = title;
            // OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
            ofn.flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008;

            if (!GetOpenFileName(ofn)) return null;   // 사용자가 취소했거나 실패

            // 네이티브가 널 종료 문자열을 돌려주므로 첫 \0 이후를 잘라낸다.

            string result = ofn.file ?? "";
            int nul = result.IndexOf('\0');
            if (nul >= 0) result = result.Substring(0, nul);
            return string.IsNullOrEmpty(result) ? null : result;
        }
#else
        public static string Open(string title = "")
        {
            Debug.LogWarning("[WindowsFileDialog] 현재 플랫폼에서는 네이티브 파일창을 지원하지 않습니다.");
            return null;
        }
#endif
    }
}