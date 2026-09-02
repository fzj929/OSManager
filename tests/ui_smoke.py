from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent / ".deps"))
from playwright.sync_api import sync_playwright

root = Path(__file__).resolve().parents[1]
artifacts = root / "test-artifacts"
artifacts.mkdir(exist_ok=True)

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r"C:\Program Files\Google\Chrome\Application\chrome.exe")
    page = browser.new_page(viewport={"width": 1440, "height": 1000}, device_scale_factor=1)
    errors = []
    page.on("console", lambda msg: errors.append(msg.text) if msg.type == "error" else None)
    page.on("pageerror", lambda exc: errors.append(str(exc)))
    page.goto("http://127.0.0.1:5080", wait_until="networkidle")
    assert page.get_by_text("主机在手").is_visible()
    page.get_by_role("button", name="进入控制台 →").click()
    page.wait_for_selector("text=运行概览")
    page.wait_for_load_state("networkidle")
    assert page.get_by_text("系统运行平稳").is_visible()
    assert page.get_by_text("CPU 使用率").is_visible()
    page.screenshot(path=str(artifacts / "dashboard.png"), full_page=True)
    page.get_by_role("button", name="文件发布").click()
    page.wait_for_selector("text=最近发布")
    assert page.get_by_text("选择文件或 ZIP").is_visible()
    page.get_by_role("button", name="日志搜索").click()
    page.wait_for_selector("text=普通文件日志")
    page.screenshot(path=str(artifacts / "logs.png"), full_page=True)
    if errors:
        raise AssertionError("Browser errors: " + " | ".join(errors))
    print("UI_SMOKE_OK dashboard, files, logs; console_errors=0")
    browser.close()
