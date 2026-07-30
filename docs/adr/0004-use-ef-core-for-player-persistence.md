# ADR 0004: Player 영속성 계층에 EF Core를 사용한다

- 상태: 승인됨(Accepted)
- 결정일: 2026-07-30

## 맥락

Player는 닉네임, 생성 시각, 수정 시각처럼 서버를 재시작해도 남아야 하는 정보를 가집니다.
이 프로젝트는 C# 객체인 `Player`와 PostgreSQL의 `players` 테이블을 연결하고, 테이블 구조 변경도 Git 이력으로 관리해야 합니다.

데이터 접근 방식으로 EF Core(Entity Framework Core, C# 객체와 관계형 데이터베이스를 연결하는 ORM)와 Dapper(대부분의 SQL을 개발자가 직접 작성하는 경량 데이터 접근 도구)를 비교했습니다.

## 결정

Player 프로필과 이후 초기 게임 데이터의 영속성 계층에는 EF Core와 Npgsql PostgreSQL 제공자를 사용합니다.

- `GameDbContext`에 `DbSet<Player>`를 등록합니다.
- Entity 매핑은 `OnModelCreating`에 명시합니다.
- 테이블 구조 변경은 EF Core Migration으로 만들고 Git에 커밋합니다.
- HTTP 요청 단위의 DB 작업에는 `SaveChangesAsync`와 `CancellationToken`을 사용합니다.

## 선택 이유

- C# Entity와 PostgreSQL 열·기본 키·인덱스·길이 규칙을 한 곳에서 확인할 수 있습니다.
- Migration은 `players` 같은 테이블 변경을 재현 가능한 C# 이력으로 남깁니다.
- 현재 단계의 Player처럼 단순한 생성·조회·수정은 LINQ(Language Integrated Query, C# 안에서 데이터를 질의하는 문법)와 변경 추적으로 읽기 쉽습니다.
- xUnit 테스트에서 InMemory 제공자를 사용해 컨트롤러의 기본 저장 흐름을 빠르게 검증할 수 있습니다.

## 고려한 대안

### Dapper

Dapper는 SQL을 직접 작성하고 결과를 객체에 매핑하는 경량 도구입니다.

- 장점: 복잡한 조회 SQL과 대량 읽기·쓰기에서 실행 SQL을 명시적으로 제어하기 쉽습니다.
- 단점: 테이블 매핑, Migration, 반복적인 CRUD(Create, Read, Update, Delete, 생성·조회·수정·삭제) 코드를 별도 방식으로 관리해야 합니다.

현재는 DB 모델이 자주 바뀌고 학습 목적상 Migration·매핑·변경 추적을 함께 익혀야 하므로 EF Core를 선택합니다.

### 직접 Npgsql SQL 작성

Npgsql 드라이버로 SQL을 직접 실행할 수도 있습니다.

- 장점: 의존성이 작고 SQL 동작이 가장 명시적입니다.
- 단점: 매개변수 처리, 매핑, 트랜잭션, 반복 코드의 책임이 모두 애플리케이션으로 이동합니다.

프로젝트 초기에 직접 구현해야 할 기반 코드가 많아지는 단점이 더 크다고 판단했습니다.

## 결과와 제약

- 기본 도메인 저장·조회는 EF Core를 사용합니다.
- 읽기 전용 단건 조회에는 `AsNoTracking()`을 사용해 불필요한 변경 추적 비용을 줄입니다.
- EF Core를 사용해도 생성되는 SQL, 인덱스 사용, N+1 쿼리 문제는 계속 확인해야 합니다.
- 복잡한 통계·랭킹·대량 배치처럼 SQL 제어가 더 중요한 구간은 이후 Dapper 또는 직접 SQL을 제한적으로 검토할 수 있습니다.
- InMemory 테스트는 PostgreSQL의 UNIQUE 제약 조건을 완전히 재현하지 못하므로, 중복 처리·트랜잭션은 PostgreSQL 통합 테스트로 검증합니다.
