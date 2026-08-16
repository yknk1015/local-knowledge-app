BEGIN IMMEDIATE;

ALTER TABLE categories ADD COLUMN management_code TEXT;
ALTER TABLE articles ADD COLUMN management_code TEXT;

CREATE TABLE management_code_sequences (
    entity_type TEXT PRIMARY KEY CHECK (entity_type IN ('category', 'article')),
    next_value INTEGER NOT NULL CHECK (next_value >= 1)
);

WITH numbered AS (
    SELECT id, ROW_NUMBER() OVER (ORDER BY created_at, id) AS sequence_number
      FROM categories
)
UPDATE categories
   SET management_code = (
       SELECT 'CAT-' || printf('%05d', sequence_number)
         FROM numbered
        WHERE numbered.id = categories.id
   );

WITH numbered AS (
    SELECT id, ROW_NUMBER() OVER (ORDER BY created_at, id) AS sequence_number
      FROM articles
)
UPDATE articles
   SET management_code = (
       SELECT 'FAQ-' || printf('%05d', sequence_number)
         FROM numbered
        WHERE numbered.id = articles.id
   );

INSERT INTO management_code_sequences(entity_type, next_value)
VALUES
    ('category', (SELECT COUNT(*) + 1 FROM categories)),
    ('article', (SELECT COUNT(*) + 1 FROM articles));

CREATE UNIQUE INDEX uq_categories_management_code
    ON categories(management_code);
CREATE UNIQUE INDEX uq_articles_management_code
    ON articles(management_code);

CREATE TRIGGER assign_category_management_code
AFTER INSERT ON categories
WHEN NEW.management_code IS NULL OR NEW.management_code = ''
BEGIN
    UPDATE categories
       SET management_code = 'CAT-' || printf(
           '%05d',
           (SELECT next_value FROM management_code_sequences WHERE entity_type = 'category')
       )
     WHERE id = NEW.id;
    UPDATE management_code_sequences
       SET next_value = next_value + 1
     WHERE entity_type = 'category';
END;

CREATE TRIGGER assign_article_management_code
AFTER INSERT ON articles
WHEN NEW.management_code IS NULL OR NEW.management_code = ''
BEGIN
    UPDATE articles
       SET management_code = 'FAQ-' || printf(
           '%05d',
           (SELECT next_value FROM management_code_sequences WHERE entity_type = 'article')
       )
     WHERE id = NEW.id;
    UPDATE management_code_sequences
       SET next_value = next_value + 1
     WHERE entity_type = 'article';
END;

INSERT OR IGNORE INTO schema_migrations(version, applied_at)
VALUES (7, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

COMMIT;
