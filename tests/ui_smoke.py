from pathlib import Path
import json
import os
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent / ".deps"))
from playwright.sync_api import sync_playwright

root = Path(__file__).resolve().parents[1]
artifacts = root / "test-artifacts"
artifacts.mkdir(exist_ok=True)
base_url = os.environ.get("OSMANAGER_UI_BASE_URL", "http://127.0.0.1:5080")
test_log_source = os.environ.get("OSMANAGER_UI_TEST_LOG_SOURCE", "")

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r"C:\Program Files\Google\Chrome\Application\chrome.exe")
    page = browser.new_page(viewport={"width": 1440, "height": 1000}, device_scale_factor=1)
    errors = []
    page.on("console", lambda msg: errors.append(msg.text) if msg.type == "error" else None)
    page.on("pageerror", lambda exc: errors.append(str(exc)))
    page.on("response", lambda response: errors.append(f"HTTP {response.status}: {response.url}") if response.status >= 400 else None)

    def resources_with_test_service(route):
        response = route.fetch()
        data = response.json()
        data["services"] = list(dict.fromkeys([*data.get("services", []), "scroll-test.service"]))
        data["directories"] = [*data.get("directories", []), {
            "id": "ui-config", "name": "UI 配置目录", "path": "/tmp/ui-config",
            "canUpload": True, "relatedService": "", "backupExcludes": ""
        }]
        headers = {key: value for key, value in response.headers.items() if key.lower() != "content-length"}
        headers["content-type"] = "application/json; charset=utf-8"
        route.fulfill(status=response.status, headers=headers, body=json.dumps(data))

    def journal_stream(route):
        events = "".join(
            f'data: {json.dumps({"MESSAGE": f"live line {index}", "PRIORITY": "6"})}\n\n'
            for index in range(1, 301)
        )
        route.fulfill(status=200, headers={"content-type": "text/event-stream"}, body=events)

    page.route("**/api/resources", resources_with_test_service)
    page.route("**/api/files?*", lambda route: route.fulfill(status=200, content_type="application/json", body=json.dumps({"items":[{"name":"releases","path":"releases","isDirectory":True,"size":0,"modified":"2026-09-07T00:00:00Z"},{"name":"appsettings.json","path":"appsettings.json","isDirectory":False,"size":128,"modified":"2026-09-07T00:00:00Z"}]})))
    page.route("**/api/journal/follow?service=scroll-test.service", journal_stream)
    page.route("**/api/configs/files?rootId=ui-config", lambda route: route.fulfill(status=200, content_type="application/json", body='["appsettings.json","conf/app.yaml"]'))
    page.route("**/api/configs/content?rootId=ui-config&path=appsettings.json", lambda route: route.fulfill(status=200, content_type="application/json", body='{"content":"{\\n  \\"enabled\\": true\\n}","version":"UI-VERSION"}'))
    page.goto(base_url, wait_until="networkidle")
    assert page.get_by_text("主机在手").is_visible()
    page.get_by_role("button", name="进入控制台 →").click()
    page.wait_for_selector("text=运行概览")
    page.wait_for_load_state("networkidle")
    assert page.get_by_text("系统运行平稳").is_visible()
    assert page.get_by_text("CPU 使用率").is_visible()
    page.screenshot(path=str(artifacts / "dashboard.png"), full_page=True)
    page.get_by_role("button", name="文件发布").click()
    page.wait_for_selector("text=变更与备份")
    assert page.get_by_text("选择文件或 ZIP").is_visible()
    file_row = page.locator("tbody tr").filter(has_text="appsettings.json")
    assert file_row.get_by_role("button", name="下载").is_visible()
    assert file_row.get_by_role("button", name="替换").is_visible()
    file_row.get_by_role("button", name="替换").click()
    assert page.get_by_text("替换文件", exact=True).is_visible()
    assert page.get_by_role("button", name="备份并替换").is_disabled()
    page.locator(".replace-modal").get_by_role("button", name="关闭").click()
    folder_row = page.locator("tbody tr").filter(has_text="releases")
    folder_row.get_by_role("button", name="替换").click()
    assert page.get_by_text("替换文件夹", exact=True).is_visible()
    assert page.locator(".replacement-picker input").get_attribute("accept") == ".zip,application/zip"
    page.screenshot(path=str(artifacts / "folder-replace.png"), full_page=True)
    page.locator(".replace-modal").get_by_role("button", name="关闭").click()
    file_row.get_by_role("button", name="删除").click()
    assert page.get_by_text("删除文件", exact=True).is_visible()
    assert page.get_by_role("button", name="直接删除").is_visible()
    assert page.get_by_role("button", name="备份后删除").is_visible()
    page.screenshot(path=str(artifacts / "file-delete-confirm.png"), full_page=True)
    page.locator(".delete-modal").get_by_role("button", name="取消").click()
    assert not page.locator(".delete-modal").is_visible()
    page.get_by_role("button", name="日志查看").click()
    page.wait_for_selector("text=普通文件日志")
    assert page.get_by_role("tab", name="文件日志").get_attribute("aria-selected") == "true"
    assert page.get_by_text("匹配结果").is_visible()
    assert page.get_by_placeholder("留空显示时间范围内的全部内容").is_visible()
    page.get_by_role("button", name="收起条件").click()
    page.wait_for_timeout(250)
    assert not page.get_by_placeholder("留空显示时间范围内的全部内容").is_visible()
    page.screenshot(path=str(artifacts / "logs-collapsed.png"), full_page=True)
    page.get_by_role("button", name="展开条件").click()
    page.wait_for_timeout(250)
    assert page.get_by_placeholder("留空显示时间范围内的全部内容").is_visible()
    if test_log_source:
        page.locator("select").first.select_option(test_log_source)
        page.get_by_placeholder("留空显示时间范围内的全部内容").fill("MATCH-CONTEXT")
        page.get_by_role("button", name="开始搜索").click()
        page.wait_for_selector("text=MATCH-CONTEXT")
        page.get_by_role("button", name="查看上下文").click()
        page.wait_for_selector("text=命中第 4 行")
        assert page.locator(".context-lines .hit").get_by_text("MATCH-CONTEXT").is_visible()
        page.screenshot(path=str(artifacts / "log-context.png"), full_page=True)
        page.get_by_role("button", name="关闭").click()
        with page.expect_download() as download_info:
            page.get_by_role("button", name="下载文件").click()
        assert download_info.value.suggested_filename == "application.log"
    page.screenshot(path=str(artifacts / "logs.png"), full_page=True)
    page.get_by_role("tab", name="Journal 日志").click()
    page.wait_for_selector("text=Journal 日志")
    assert page.locator(".journal-panel .form-grid").is_visible()
    page.get_by_role("button", name="收起条件").click()
    assert not page.locator(".journal-panel .form-grid").is_visible()
    page.screenshot(path=str(artifacts / "journal-collapsed.png"), full_page=True)
    page.get_by_role("button", name="展开条件").click()
    assert page.locator(".journal-panel .form-grid").is_visible()
    page.locator(".query .form-grid select").select_option("scroll-test.service")
    page.get_by_role("button", name="实时滚动").click()
    page.wait_for_selector("text=live line 300")
    page.wait_for_timeout(100)
    at_bottom = page.locator(".journal").evaluate("element => Math.abs(element.scrollHeight - element.clientHeight - element.scrollTop) <= 2")
    assert at_bottom
    page.screenshot(path=str(artifacts / "journal-follow.png"), full_page=True)
    page.get_by_role("button", name="进程管理").click()
    page.wait_for_selector("text=进程快照")
    process_search = page.get_by_placeholder("输入进程名称或 PID")
    assert process_search.is_visible()
    process_search.fill("a-process-that-does-not-exist")
    assert page.get_by_text("没有匹配的进程").is_visible()
    process_search.fill("")
    page.get_by_role("button", name="配置文件").click()
    page.wait_for_selector("text=选择配置文件")
    page.locator(".config-picker select").select_option("ui-config")
    assert page.get_by_text("2 FILES").is_visible()
    config_path = page.get_by_placeholder("从列表选择或手动输入")
    config_path.fill("appsettings.json")
    page.get_by_role("button", name="读取").click()
    page.wait_for_selector("text=已读取，可直接编辑保存")
    assert '"enabled": true' in page.locator(".config-editor textarea").input_value()
    assert page.get_by_role("button", name="保存配置").is_enabled()
    assert not page.get_by_text("审批队列").is_visible()
    page.screenshot(path=str(artifacts / "config-editor.png"), full_page=True)
    page.get_by_role("button", name="服务管理").click()
    page.wait_for_selector("text=服务管理")
    assert not page.locator(".journal-panel").is_visible()
    assert not page.locator(".service-modal").is_visible()
    assert page.get_by_role("button", name="添加服务", exact=True).is_visible()
    assert page.locator(".service-grid > :last-child").get_attribute("aria-label") == "添加服务"
    page.get_by_role("button", name="添加服务", exact=True).click()
    assert page.locator(".service-modal").is_visible()
    assert page.get_by_role("tab", name="添加已有服务").get_attribute("aria-selected") == "true"
    assert page.get_by_placeholder("ihr.service").is_visible()
    page.get_by_role("tab", name="上传 ZIP 安装服务").click()
    assert page.get_by_placeholder("/app/publish-v2.2").is_visible()
    assert page.get_by_placeholder("IHR.Api.dll").is_visible()
    assert page.get_by_text("仅支持 ZIP，最大 500MB").is_visible()
    page.screenshot(path=str(artifacts / "services-publish.png"), full_page=True)
    page.locator(".service-modal").get_by_role("button", name="关闭").click()
    assert not page.locator(".service-modal").is_visible()
    page.get_by_role("button", name="资源设置").click()
    page.wait_for_selector("text=新增管理目录")
    assert not page.get_by_text("systemd 服务白名单").is_visible()
    assert page.get_by_text("新增管理目录").is_visible()
    assert page.get_by_placeholder("/opt/apps/app-a").is_visible()
    assert page.get_by_placeholder("logs;files;download").is_visible()
    assert page.get_by_text("新增日志源").is_visible()
    assert page.get_by_placeholder("/app/publish-v2.2/logs").is_visible()
    page.screenshot(path=str(artifacts / "settings.png"), full_page=True)
    page.get_by_role("button", name="用户管理").click()
    page.wait_for_selector("text=本地用户")
    page.get_by_role("button", name="添加用户").click()
    assert page.get_by_text("添加本地用户").is_visible()
    page.screenshot(path=str(artifacts / "users.png"), full_page=True)
    page.locator(".modal-head button").click()
    if errors:
        raise AssertionError("Browser errors: " + " | ".join(errors))
    print("UI_SMOKE_OK dashboard, files, logs, processes, config-editor, services, settings, user-modal; console_errors=0")
    browser.close()
