# 隐私说明 / Privacy

**中文**

「桌面图标跳舞」不收集、不保存、不上传你的任何数据。

- **不联网。** 程序里没有任何联网的代码，不包含广告或统计。Windows 版用了一个本地音频库 NAudio 来听系统声音，它同样不联网。
- **桌面文件：** 只向系统要桌面上文件的名字和图标（和访达 / 资源管理器里显示的一样，图片、PDF 等会是一张内容缩略图），用来画会动的副本。程序自己不读取、不保存文件内容，不会移动、改名或删除任何文件。
- **电脑正在播放的声音：** 只在内存里实时分析节拍，让图标跟着跳。不录音、不保存、不发送到任何地方。
- **保存在你电脑上的东西：** 你在小窗口里的选择（语言、当球的图标等），以及一份运行日志和一份图标位置快照，位于 `~/Library/Application Support/DesktopDance/`（Mac）或 `%LocalAppData%\DesktopDance`（Windows）。里面会有桌面图标的名字；Windows 的日志里还有壁纸文件的路径（通常带电脑用户名）。它们只在你的电脑上，删掉这个文件夹即可清除。
- **权限（Mac）：** 程序可能请求访问「桌面」文件夹、向访达询问图标位置、听取系统声音。全部可以拒绝，拒绝后程序仍能运行，只是对应的效果会缺失。不会申请麦克风、摄像头或屏幕录制。Windows 版不会弹任何权限询问。

有问题可以在本仓库提 issue。

---

**English**

DesktopDance does not collect, store or upload any of your data.

- **No network access.** The app contains no networking code, no ads and no analytics. The Windows version uses one local audio library (NAudio) to hear system audio; it does not use the network either.
- **Desktop files:** it asks the system only for the names and icons of the files on your desktop (the same ones Finder / File Explorer shows; for images, PDFs and the like that is a small preview), to draw animated copies. The app itself never reads or stores file contents and never moves, renames or deletes anything.
- **Audio your computer is playing:** analysed live in memory to find the beat, so the icons can follow it. Nothing is recorded, saved or sent anywhere.
- **What is stored on your computer:** your choices in the small window (language, which icon is the ball, and so on), plus a log file and a snapshot of icon positions, in `~/Library/Application Support/DesktopDance/` (Mac) or `%LocalAppData%\DesktopDance` (Windows). They include the names of your desktop icons; the Windows log also contains the wallpaper file path, which usually includes your Windows user name. These stay on your computer; delete that folder to remove them.
- **Permissions (Mac):** the app may ask to access the Desktop folder, to ask Finder for icon positions, and to listen to system audio. You can decline any of them; the app still runs, with the related effect missing. It never asks for the microphone, the camera or screen recording. The Windows version shows no permission prompts.

Questions are welcome as issues in this repository.
