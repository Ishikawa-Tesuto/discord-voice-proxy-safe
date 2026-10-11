# Установка файлов DVP Safe в форк discord-voice-proxy на Arch Linux

**Это оверлей, а не отдельный Git-репозиторий.** Сначала сделайте Fork официального `runetfreedom/discord-voice-proxy` в GitHub. В `master` вашего форка заменятся только `proxy-dll/dllmain.cpp`, а остальные файлы из этого оверлея будут добавлены.

## 1. Локально на Arch Linux

```bash
sudo pacman -S --needed git github-cli python unzip
# Вариант 1 — склонировать форк вручную:
git clone https://github.com/YOUR_USERNAME/discord-voice-proxy.git
cd discord-voice-proxy

# Распаковать DVP-Fork-Overlay.zip ПРЯМО в эту папку:
unzip -o /path/to/DVP-Fork-Overlay.zip

# Исправить MSVC-проект (убрать MASM после замены кода DLL):
python tools/prepare_project.py

git status
git add proxy-dll/dllmain.cpp proxy-dll/discord-voice-proxy.vcxproj .github patches tools docs
git commit -m 'Add experimental DVP Safe loader and Windows CI'
git push origin HEAD
```

`INSTALL-IN-FORK.ru.md` можно оставить локально. Если вы случайно распаковали ZIP во вложенную папку, нужно переместить его содержимое на уровень `README.md` оригинала.

## 2. Build

GitHub → ваш fork → **Actions** → **DVP Safe - Windows x64** → **Run workflow**. Также workflow запускается на `git push` в `master` или `main`. В завершённом запуске откройте **Artifacts** → `DVP-Safe-Windows-x64`.

Это будет архив с `DWrite.dll`, `force-proxy-safe.dll`, инструкцией, скриптами установки и исходным кодом для GPL-переиздания. Не нужно отдельно форкать `force-proxy`: Actions скачает его, затем применит проверяемые изменения из `tools/patch_force_proxy.py` и `patches/force-proxy/dllmain.cpp`.

## 3. Локальный запуск Actions из Arch Linux (по желанию)

```bash
gh auth login
gh workflow run build-windows.yml
gh run list --workflow build-windows.yml
gh run watch <RUN_ID> --exit-status
gh run download <RUN_ID> --name DVP-Safe-Windows-x64 --dir ./output
```

Если название Actions не появляется, проверьте, что файл `.github/workflows/build-windows.yml` запушен в **default branch** форка, а Actions включены в настройках репозитория.

**Внимание:** C++ компиляция в Windows Actions пока не проходила. Ошибки реальной компиляции и работы Discord не исключены. Не отправляйте DLL другу как проверенную рабочую версию до хотя бы одного успешного теста.
