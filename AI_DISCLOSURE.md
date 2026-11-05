# AI 生成与验证说明 / AI disclosure

## 来源与生成方式

本增强版基于 [PukerWonderLand/codex-usage](https://github.com/PukerWonderLand/codex-usage)，以提交 `19a2e96e945f8c1bcc7cebd24a22a1133ff22db0` 为修改基准。保留上游 MIT 许可证及版权声明；本声明不取代许可证，也不改变上游作者署名。

本增强版的新增与修改代码、测试及部分文档主要由生成式 AI（OpenAI Codex）根据使用者要求生成、修改和调试，使用者参与了需求、试用及问题反馈。此声明仅描述本增强版的改动，不应推断上游代码也由 AI 生成。

本项目为实验性独立社区项目，与 OpenAI 无隶属关系，未获其认可或背书。“Codex”用于描述兼容的产品，不能据此推断官方身份。

## 已验证范围

截至 2026-11-05，本增强版自动测试为 93 项通过、0 项失败、1 项跳过；Windows 系统 C# 编译通过。视觉跟随已在 Windows Store 版 Codex、浅色主题、150% 显示缩放下进行部分实机验证，包括聊天切换、侧栏隐藏与恢复以及前台状态检查。这些结果不代表完整兼容性验证、独立人工代码审查或安全审计。

当前聊天识别依赖可见侧栏、选中颜色、Windows 无障碍及 OCR；重名、标题截断、窗口遮挡或界面变化可能导致无法识别。画面无法确认时应显示未识别。深色主题、其他缩放和其他应用分发版本尚未完整实测。

上下文参考占用来自日志中最近请求输入 Token 与上报窗口的比例，不代表实时剩余容量或压缩阈值。API 等价费用估算不代表订阅账单。

识别组件在连续 10 秒无身份请求后退出。其他聊天中的内嵌网页若仍在轮询，会保持组件运行；仅切换聊天不保证休眠。性能结果是单机短时采样，不承诺固定占用或降幅。

## English

This enhancement is derived from PukerWonderLand/codex-usage at commit `19a2e96e945f8c1bcc7cebd24a22a1133ff22db0`. The upstream MIT license and copyright notice are retained.

Most added or modified code, tests, and some documentation in this enhancement were generated and iterated by OpenAI Codex from user instructions. The user supplied requirements, tried the software, and reported issues. This disclosure applies to the enhancement's changes, not the authorship of upstream code.

This is an experimental independent community project, unaffiliated with and not endorsed by OpenAI. As of 2026-11-05, automated tests reported 93 passes, zero failures, and one skip. Windows C# compilation succeeded. Selected desktop scenarios were tested with the Windows Store distribution of Codex, a light theme, and 150% display scaling. These checks do not constitute comprehensive compatibility testing, independent human code review, or a security audit.

Visual identification can fail when titles are duplicated or truncated, the sidebar is hidden, the window is obscured, or the application UI changes. Reported context usage and costs are log-derived estimates. Other polling pages can keep the visual helper awake. No fixed resource usage or performance improvement is guaranteed.
