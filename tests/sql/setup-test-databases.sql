-- Идемпотентный скрипт подготовки тестовых БД для интеграционных тестов.
-- Создаёт две БД (sql-data-mover-src, sql-data-mover-dest) со схемой tests
-- и связанными внешними ключами таблицами: Customers -> Orders -> OrderItems,
-- а также самоссылающуюся Employees.

IF DB_ID('sql-data-mover-src') IS NULL
    CREATE DATABASE [sql-data-mover-src];
GO

IF DB_ID('sql-data-mover-dest') IS NULL
    CREATE DATABASE [sql-data-mover-dest];
GO

-- ============ ИСТОЧНИК ============
USE [sql-data-mover-src];
GO

IF SCHEMA_ID('tests') IS NULL
    EXEC('CREATE SCHEMA tests');
GO

IF OBJECT_ID('tests.OrderItems', 'U') IS NOT NULL DROP TABLE tests.OrderItems;
IF OBJECT_ID('tests.Orders', 'U') IS NOT NULL DROP TABLE tests.Orders;
IF OBJECT_ID('tests.Employees', 'U') IS NOT NULL DROP TABLE tests.Employees;
IF OBJECT_ID('tests.Customers', 'U') IS NOT NULL DROP TABLE tests.Customers;
GO

CREATE TABLE tests.Customers
(
    Id     INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
    Region NVARCHAR(20)       NOT NULL,
    Code   NVARCHAR(20)       NOT NULL,
    Name   NVARCHAR(100)      NOT NULL,
    CONSTRAINT UQ_Customers_Region_Code UNIQUE (Region, Code)
);

CREATE TABLE tests.Orders
(
    Id         INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
    CustomerId INT                NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES tests.Customers (Id),
    OrderDate  DATE               NOT NULL
);

CREATE TABLE tests.OrderItems
(
    Id      INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_OrderItems PRIMARY KEY,
    OrderId INT                NOT NULL CONSTRAINT FK_OrderItems_Orders REFERENCES tests.Orders (Id),
    Product NVARCHAR(100)      NOT NULL,
    Qty     INT                NOT NULL
);

CREATE TABLE tests.Employees
(
    Id        INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Employees PRIMARY KEY,
    Name      NVARCHAR(100)      NOT NULL,
    ManagerId INT                NULL CONSTRAINT FK_Employees_Manager REFERENCES tests.Employees (Id)
);
GO

-- ============ ПРИЁМНИК ============
USE [sql-data-mover-dest];
GO

IF SCHEMA_ID('tests') IS NULL
    EXEC('CREATE SCHEMA tests');
GO

IF OBJECT_ID('tests.OrderItems', 'U') IS NOT NULL DROP TABLE tests.OrderItems;
IF OBJECT_ID('tests.Orders', 'U') IS NOT NULL DROP TABLE tests.Orders;
IF OBJECT_ID('tests.Employees', 'U') IS NOT NULL DROP TABLE tests.Employees;
IF OBJECT_ID('tests.Customers', 'U') IS NOT NULL DROP TABLE tests.Customers;
GO

CREATE TABLE tests.Customers
(
    Id     INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
    Region NVARCHAR(20)       NOT NULL,
    Code   NVARCHAR(20)       NOT NULL,
    Name   NVARCHAR(100)      NOT NULL,
    CONSTRAINT UQ_Customers_Region_Code UNIQUE (Region, Code)
);

CREATE TABLE tests.Orders
(
    Id         INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
    CustomerId INT                NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES tests.Customers (Id),
    OrderDate  DATE               NOT NULL
);

CREATE TABLE tests.OrderItems
(
    Id      INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_OrderItems PRIMARY KEY,
    OrderId INT                NOT NULL CONSTRAINT FK_OrderItems_Orders REFERENCES tests.Orders (Id),
    Product NVARCHAR(100)      NOT NULL,
    Qty     INT                NOT NULL
);

CREATE TABLE tests.Employees
(
    Id        INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Employees PRIMARY KEY,
    Name      NVARCHAR(100)      NOT NULL,
    ManagerId INT                NULL CONSTRAINT FK_Employees_Manager REFERENCES tests.Employees (Id)
);
GO
