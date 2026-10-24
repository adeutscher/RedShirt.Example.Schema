# MariaDB Table Reminders

> **Reminder document.** This is a quick reference for common MariaDB table patterns. It is not a migration script and
> is not run by DbUp. Real schema changes belong in `src/RedShirt.Example.Schema/Scripts/`, following the
> [File Organization and Maintenance](../README.md#file-organization-and-maintenance) section of the README.

## Basic Table

The example below follows the same conventions as `0000-00-00-00-example.sql`:

* Backtick-quoted table and column names.
* `CHAR(36)` UUID identifiers.
* `DATETIME(6)` timestamps stored in UTC.
* `DECIMAL(19, 4)` for money.
* Index names in the form `IX_<Table>_<Column>`.

```sql
CREATE TABLE IF NOT EXISTS `Widget`
(
    `Id` CHAR(36) NOT NULL,
    `CreatedAtUtc` DATETIME(6) NOT NULL,
    `UpdatedAtUtc` DATETIME(6) NOT NULL,
    `Code` VARCHAR(64) NOT NULL,
    `Name` VARCHAR(255) NOT NULL,
    `Price` DECIMAL(19, 4) NOT NULL,
    `Notes` TEXT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `IX_Widget_Code` (`Code`),
    KEY `IX_Widget_Name` (`Name`)
);
```

Key types at a glance:

* **PRIMARY KEY**: Uniquely identifies each row. One per table, implies `NOT NULL`.
* **UNIQUE KEY**: Enforces uniqueness on one or more columns. Multiple `NULL` values are allowed.
* **KEY** (or **INDEX**): Plain index for faster lookups. No uniqueness rule.

## Constraints

Constraints are rules that the server enforces on every insert and update.

### Column Constraints

```sql
CREATE TABLE IF NOT EXISTS `Account`
(
    `Id` CHAR(36) NOT NULL,                    -- value required
    `Email` VARCHAR(320) NOT NULL,
    `Balance` DECIMAL(19, 4) NOT NULL DEFAULT 0, -- used when the insert omits the column
    `Flags` INT NOT NULL DEFAULT 0,
    `ClosedAtUtc` DATETIME(6) NULL,            -- optional value
    PRIMARY KEY (`Id`),
    UNIQUE KEY `IX_Account_Email` (`Email`)
);
```

### Unique Constraints

A unique constraint rejects any insert or update that would create a duplicate value. MariaDB backs it with a unique
index, so `UNIQUE KEY` and `CONSTRAINT ... UNIQUE` behave the same.

```sql
CREATE TABLE IF NOT EXISTS `Subscription`
(
    `Id` CHAR(36) NOT NULL,
    `CustomerId` CHAR(36) NOT NULL,
    `PlanCode` VARCHAR(64) NOT NULL,
    `ExternalRef` VARCHAR(128) NULL,
    PRIMARY KEY (`Id`),
    -- single column: no two rows may share an ExternalRef
    UNIQUE KEY `IX_Subscription_ExternalRef` (`ExternalRef`),
    -- composite: a customer may hold each plan once, but different customers may share a plan
    CONSTRAINT `IX_Subscription_CustomerId_PlanCode` UNIQUE (`CustomerId`, `PlanCode`)
);
```

Things to remember:

* `NULL` is never equal to another `NULL`, so a nullable unique column accepts many `NULL` rows. In a composite key,
  any row with a `NULL` in one of the columns is not checked against the others.
* Column order in a composite key matters for lookups. The index above also speeds up queries that filter on
  `CustomerId` alone, but not on `PlanCode` alone.
* Uniqueness follows the column collation. With a case-insensitive collation (the default), `Bob@example.com` and
  `bob@example.com` count as duplicates. Use a binary collation such as `ascii_bin` when case must matter.
* Adding a unique key to an existing table fails if duplicates already exist. Find them first:

    ```sql
    SELECT `CustomerId`, `PlanCode`, COUNT(*) AS `Count`
    FROM `Subscription`
    GROUP BY `CustomerId`, `PlanCode`
    HAVING COUNT(*) > 1;
    ```

* `INSERT ... ON DUPLICATE KEY UPDATE` and `INSERT IGNORE` react to unique keys as well as the primary key.

### CHECK Constraints

MariaDB enforces `CHECK` constraints since version 10.2.1. Name them so that errors and later `ALTER` statements are
easy to follow.

```sql
CREATE TABLE IF NOT EXISTS `Shipment`
(
    `Id` CHAR(36) NOT NULL,
    `Weight` DECIMAL(10, 3) NOT NULL,
    `Status` VARCHAR(32) NOT NULL,
    `ShippedAtUtc` DATETIME(6) NULL,
    `DeliveredAtUtc` DATETIME(6) NULL,
    PRIMARY KEY (`Id`),
    CONSTRAINT `CK_Shipment_Weight` CHECK (`Weight` > 0),
    CONSTRAINT `CK_Shipment_Status` CHECK (`Status` IN ('Pending', 'Shipped', 'Delivered')),
    CONSTRAINT `CK_Shipment_Dates` CHECK (`DeliveredAtUtc` IS NULL OR `DeliveredAtUtc` >= `ShippedAtUtc`)
);
```

A `CHECK` that evaluates to `NULL` passes. In the `CK_Shipment_Dates` example, a delivered row with a `NULL`
`ShippedAtUtc` would be accepted. Add an explicit `IS NOT NULL` test if that matters.

### Adding and Dropping Constraints

```sql
ALTER TABLE `Shipment`
    ADD CONSTRAINT `CK_Shipment_WeightLimit` CHECK (`Weight` <= 1000);

ALTER TABLE `Shipment`
    DROP CONSTRAINT `CK_Shipment_WeightLimit`;

ALTER TABLE `Account`
    ADD UNIQUE KEY `IX_Account_Flags_Email` (`Flags`, `Email`);

ALTER TABLE `Account`
    DROP INDEX `IX_Account_Flags_Email`;
```

## Foreign Keys

A foreign key links a column in a child table to a key in a parent table. The server rejects child rows that point at a
parent row that does not exist.

The template tables in `0000-00-00-00-example.sql` do not currently declare foreign keys. The examples below show how
they could.

```sql
CREATE TABLE IF NOT EXISTS `Customers`
(
    `Id` CHAR(36) NOT NULL,
    `Email` VARCHAR(320) NOT NULL,
    PRIMARY KEY (`Id`)
);

CREATE TABLE IF NOT EXISTS `Order`
(
    `Id` CHAR(36) NOT NULL,
    `CustomerId` CHAR(36) NOT NULL,
    `TotalAmount` DECIMAL(19, 4) NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_Order_CustomerId` (`CustomerId`),
    CONSTRAINT `FK_Order_Customers` FOREIGN KEY (`CustomerId`) REFERENCES `Customers` (`Id`)
);
```

Adding a foreign key to an existing table:

```sql
ALTER TABLE `UploadEvent`
    ADD CONSTRAINT `FK_UploadEvent_UploadAggregate`
        FOREIGN KEY (`UploadId`) REFERENCES `UploadAggregate` (`Id`);

ALTER TABLE `UploadEvent`
    DROP FOREIGN KEY `FK_UploadEvent_UploadAggregate`;
```

Requirements and gotchas:

* Both tables must use InnoDB, which is the MariaDB default.
* The child and parent columns must have the same type and size. String columns must also share a character set and
  collation. `CHAR(36)` pointing at `VARCHAR(36)` fails, for example.
* The parent column must be indexed, normally as its `PRIMARY KEY` or a `UNIQUE KEY`.
* The child column needs an index. MariaDB creates one automatically if none exists, but declaring it with the usual
  `IX_<Table>_<Column>` name keeps naming consistent.
* A nullable child column allows "no parent". Only non-`NULL` values are checked.
* Existing rows must already satisfy the key when it is added with `ALTER TABLE`, or the statement fails.
* `SET FOREIGN_KEY_CHECKS = 0` turns off checking for the session, which can help with bulk loads. Remember to set it
  back to `1`. Rows inserted while it was off are not re-checked.

## Cascades

A cascade rule tells the server what to do with child rows when the referenced parent row is deleted or its key is
updated. Rules are set separately for `ON DELETE` and `ON UPDATE`. If a rule is omitted, it defaults to `RESTRICT`.

| Action        | Effect on child rows when the parent row changes          |
| ------------- | --------------------------------------------------------- |
| `CASCADE`     | Child rows are deleted or updated to match the parent.    |
| `SET NULL`    | Child column is set to NULL. The column must be nullable. |
| `RESTRICT`    | Parent change is rejected while child rows exist.         |
| `NO ACTION`   | Same as `RESTRICT` in MariaDB.                            |
| `SET DEFAULT` | Not supported by InnoDB.                                  |

### Example

```sql
CREATE TABLE IF NOT EXISTS `UploadAggregate`
(
    `Id` CHAR(36) NOT NULL,
    `FileName` VARCHAR(512) NOT NULL,
    PRIMARY KEY (`Id`)
);

CREATE TABLE IF NOT EXISTS `UploadEvent`
(
    `Id` CHAR(36) NOT NULL,
    `UploadId` CHAR(36) NOT NULL,
    `EventType` INT NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_UploadEvent_UploadId` (`UploadId`),
    CONSTRAINT `FK_UploadEvent_UploadAggregate` FOREIGN KEY (`UploadId`) REFERENCES `UploadAggregate` (`Id`)
        ON DELETE CASCADE
        ON UPDATE CASCADE
);

-- Removes the upload and every UploadEvent row that points at it.
DELETE FROM `UploadAggregate` WHERE `Id` = '00000000-0000-0000-0000-000000000001';
```

A `SET NULL` example, where orders survive their customer being removed:

```sql
CREATE TABLE IF NOT EXISTS `Order`
(
    `Id` CHAR(36) NOT NULL,
    `CustomerId` CHAR(36) NULL, -- must be nullable for SET NULL
    PRIMARY KEY (`Id`),
    KEY `IX_Order_CustomerId` (`CustomerId`),
    CONSTRAINT `FK_Order_Customers` FOREIGN KEY (`CustomerId`) REFERENCES `Customers` (`Id`)
        ON DELETE SET NULL
        ON UPDATE CASCADE
);
```

Cautions:

* `CASCADE` deletes can remove much more data than expected when tables chain together. Prefer `RESTRICT` unless the
  child rows truly have no meaning without the parent.
* Cascaded changes do not fire triggers on the child table.
* Cascades nest at most 15 levels deep. Deeper chains fail the statement.
* `ON UPDATE CASCADE` matters little for UUID keys that never change, but is harmless to declare.

## Renaming Columns

`RENAME COLUMN` (MariaDB 10.5.2 and later) changes only the name and keeps the existing definition.

```sql
ALTER TABLE `Order`
    RENAME COLUMN `TotalPrice` TO `GrandTotal`;
```

`CHANGE COLUMN` works on all versions and can rename and redefine in one statement. The full column definition must be
repeated, or any attribute left out (such as `NOT NULL` or `DEFAULT`) is lost.

```sql
ALTER TABLE `Order`
    CHANGE COLUMN `TotalPrice` `GrandTotal` DECIMAL(19, 4) NULL;
```

Related renames:

```sql
-- index names are not updated when a column is renamed
ALTER TABLE `Order`
    RENAME INDEX `IX_Order_TotalPrice` TO `IX_Order_GrandTotal`;

-- foreign keys cannot be renamed, so drop and re-add them
ALTER TABLE `Order`
    DROP FOREIGN KEY `FK_Order_Customers`,
    ADD CONSTRAINT `FK_Order_Customer` FOREIGN KEY (`CustomerId`) REFERENCES `Customers` (`Id`);

-- whole table
RENAME TABLE `Order` TO `CustomerOrder`;
```

Things to remember:

* Indexes, unique keys, and foreign keys keep working after a column rename. Only their names go stale.
* Code that reads the column by name, such as DTOs and queries in the API, breaks until it is updated. Plan the rename
  alongside the code change, or add the new column, copy the data, and drop the old column in a later script.
* With DbUp, a rename is a new update script. Never edit the script that originally created the column.
