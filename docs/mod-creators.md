# For mod creators: `ghpcmm.json`

You can tell the GHPC Mod Manager which other mods go well with yours. Put a file called `ghpcmm.json` in the root of
the GitHub repository your mod is released from (the same repo the Mod Manager downloads your releases from). No account
or permission from anyone is needed: only you can edit your repo.

The Mod Manager reads it when someone opens your mod's page and shows a **"Recommended with this mod"** card.
Changes show up within a day (the file is cached for 24 hours).

## Example

```json
{
  "Recommends": [
    "https://github.com/someone/their-sound-mod",
    {
      "Mod": "https://github.com/someone-else/their-vehicle-pack",
      "Note": {
        "en-US": "The NPzK-140 is built around these sounds.",
        "zh-CN": "NPzK-140 是按这些音效制作的。"
      }
    }
  ]
}
```

## How to point at a mod

Any of these work, use whatever you know:

- its **GitHub link**: `https://github.com/owner/repo` (or just `owner/repo`)
- its **name** as shown in the Mod Manager: `Fulda Expansion Modpack`
- its **ID** in the Mod Manager (shown under the name on the mod's page)

The mod has to be in the Mod Manager's list; anything else is skipped.

## Notes

- `Note` is optional. It can be one text for all languages (`"Note": "Built around these sounds."`) or one per
  language (`en-US`, `zh-CN`, ...).
- A recommendation is not a requirement: players see it as a suggestion with an Installed / Not installed badge and can
  open the recommended mod's page to install it.
- If the file has a mistake, the Mod Manager ignores it; your mod still works normally.
- The file can only add things shown on your mod's page. Download links always come from the Mod Manager's own list.

---

# 致Mod作者：`ghpcmm.json`

你可以告诉GHPC Mod管理器哪些Mod适合和你的Mod搭配使用。在发布Mod的GitHub仓库根目录放一个 `ghpcmm.json` 文件即可
（就是Mod管理器下载你Release的那个仓库）。不需要任何账号或审批，只有你自己能修改你的仓库。

玩家打开你的Mod详情页时，管理器会读取它并显示 **"作者推荐搭配"** 卡片。修改会在一天内生效（文件缓存24小时）。

示例见上方。指向某个Mod可以用：**GitHub链接**（或 `owner/repo`）、管理器里显示的**名称**、或管理器里的**ID**
（显示在详情页名称下方）。该Mod必须在管理器的Mod列表中，否则会被忽略。

- `Note`（推荐理由）可选，可以是一段所有语言共用的文字，也可以按语言分别填写（`en-US`、`zh-CN` 等）。
- 推荐不是依赖：玩家看到的是建议和安装状态，可以点击打开被推荐Mod的详情页安装。
- 文件格式写错时会被忽略，不影响你的Mod。
- 这个文件只能添加详情页上显示的信息，下载地址始终来自管理器自己的Mod列表。
