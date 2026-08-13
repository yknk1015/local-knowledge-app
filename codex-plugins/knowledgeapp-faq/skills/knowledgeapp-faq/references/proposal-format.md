# KnowledgeApp Codex提案形式 第2版

## 共通形式

```json
{
  "formatVersion": 2,
  "requestId": "新しいUUID",
  "seriesId": "新規はrequestIdと同じ値、修正・統合は委譲番号",
  "createdAt": "RFC 3339日時",
  "proposalKind": "create | revise | merge",
  "sourceArticles": [
    {
      "articleId": "委譲ファイルのarticleId",
      "sourceUpdatedAt": "委譲ファイルのsourceUpdatedAt"
    }
  ],
  "faq": {
    "title": "1～200文字",
    "summary": "0～500文字",
    "bodyDoc": { "type": "doc", "content": [] },
    "importance": 1
  },
  "existingCategoryCandidates": [],
  "newCategoryProposal": null
}
```

## 種類別の規則

| proposalKind | sourceArticles | 分類 | 反映方法 |
|---|---:|---|---|
| `create` | 0件 | 最大3件の既存候補または新規案1件 | 新しい下書き |
| `revise` | 委譲元1件 | 候補なし、新規案なし | 版競合確認後に既存FAQへ反映 |
| `merge` | 委譲元2～10件 | 最大3件の既存候補または新規案1件 | 元FAQを残して新しい下書き |

既存分類候補は次の形式にする。

```json
{
  "categoryId": "categories.jsonにあるUUID",
  "categoryPath": "親 > 子",
  "reason": "この分類が適する理由"
}
```

新規分類案は次の形式にする。

```json
{
  "parentCategoryId": "親分類UUID。最上位ならnull",
  "parentCategoryPath": "親の階層表示。最上位ならnull",
  "name": "新規分類名",
  "description": "分類に含めるFAQの簡潔な説明",
  "reason": "新規分類が必要な理由"
}
```

## 許可する本文JSON

- ルートは`{"type":"doc","content":[...]}`。
- ノードは`paragraph`、`text`、`hardBreak`、`heading`（level 2または3）、`bulletList`、`orderedList`、`listItem`、`table`、`tableRow`、`tableCell`、`tableHeader`を使う。
- `revise`だけは委譲元に存在する`image`ノードを同じ内容・同じ位置で維持できる。新しい画像参照を作らない。
- マークは`bold`、`italic`、`link`だけを使う。
- `link.href`はHTTPまたはHTTPSだけにする。URLを記録するだけなら通常の`text`にする。
- HTML、JavaScript、iframe、Base64、外部画像URL、ファイルURLを含めない。

## 推敲・修正

- 意味を変えずに読みやすくする依頼と、内容を訂正する依頼を区別する。
- 確認できない情報を補完して事実として書かない。
- 状態、所属分類、新着・更新期限、非表示は提案で変更しない。
- 委譲ファイルの画像ノードと添付参照をすべて維持する。

## 統合

- 重複を取り除き、一つの質問と回答として自然に読める構成にする。
- 元FAQの削除、廃止、書換えを指示しない。
- 内容が矛盾する場合は断定せず、提案本文または利用者への説明で確認点を示す。
