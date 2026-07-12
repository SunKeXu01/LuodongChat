#!/usr/bin/env python3
import email.message
import os
import smtplib
import ssl
import sys


def load_env(path: str) -> dict[str, str]:
    values: dict[str, str] = {}
    with open(path, encoding="utf-8") as handle:
        for raw in handle:
            line = raw.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            name, value = line.split("=", 1)
            values[name.strip()] = value.strip()
    return values


config = load_env(os.environ.get("ALERT_ENV_FILE", "/etc/chatgpt-connector/alert.env"))
subject = sys.argv[1] if len(sys.argv) > 1 else "ChatGPT Connector 告警"
body = sys.stdin.read().strip() or "没有提供更多故障详情。"

message = email.message.EmailMessage()
message["From"] = config["SMTP_FROM"]
message["To"] = config["ALERT_TO"]
message["Subject"] = subject
message.set_content(body)

context = ssl.create_default_context()
with smtplib.SMTP_SSL(config.get("SMTP_HOST", "smtp.qq.com"), int(config.get("SMTP_PORT", "465")), context=context, timeout=20) as smtp:
    smtp.login(config["SMTP_USERNAME"], config["SMTP_AUTH_CODE"])
    smtp.send_message(message)
