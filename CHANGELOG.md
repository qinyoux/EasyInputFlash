# 变更日志 / CHANGELOG

## 2026-09-01 — 跨工具统一治理（配置中心 / 文档站 / 测试入口）

> 非侵入式治理：本工具 `EasyInputFlash.exe` / `EasyInputFlashGUI.exe` / `flash-esp32.ps1`、状态文件、启动方式均未改动，独立运行不受影响。

### 治理内容

* **统一配置中心**：本工具的 `flash-esp32.state.json`（CLI 状态）与 `EasyInputFlashGUI.state.txt`（GUI 状态）已在 `toolshub/config/central.yaml` 登记，均为运行时自动生成、`has_secrets: false`
* **统一文档站**：本工具的 `README.md` 已在 `toolshub/docs/index.md` 登记链接
* **统一测试入口**：本工具为打包 exe，无 Python 测试，未纳入 testpaths

### 新增的治理文件（均不在本工具目录内）

* `toolshub/config/central.yaml` · `toolshub/config/config_loader.py`
* `toolshub/docs/index.md`
* `pytest.ini` · `conftest.py` · `run_tests.bat`（仓库根目录）