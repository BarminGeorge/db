using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Game.Domain;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;

namespace Tests
{
    [TestFixture]
    public class MongoUserRepositoryShould
    {
        [SetUp]
        public void SetUp()
        {
            var db = TestMongoDatabase.Create();
            db.DropCollection(MongoUserRepository.CollectionName);
            repo = new MongoUserRepository(db);
        }

        private MongoUserRepository repo;

        [Test]
        public void InsertUser()
        {
            var newUser = new UserEntity(Guid.Empty, "login", "last", "first", 2, null);
            var user = repo.Insert(newUser);
            user.Should().BeEquivalentTo(newUser, o => o.Excluding(u => u.Id));
            user.Id.Should().NotBe(Guid.Empty);
        }

        [Test]
        public void FindUserById()
        {
            var newUser = new UserEntity(Guid.Empty, "login", "last", "first", 2, Guid.NewGuid());
            var user = repo.Insert(newUser);
            var retrieved = repo.FindById(user.Id);
            retrieved.Should().BeEquivalentTo(user);
        }

        [Test]
        public void CreateUserByLogin()
        {
            var user = repo.GetOrCreateByLogin("login");
            Console.WriteLine(user.Id);
            user.Login.Should().Be("login");
            user.Id.Should().NotBe(Guid.Empty);
        }

        [Test]
        public void DontCreateUserWithSameLogin()
        {
            var user = repo.GetOrCreateByLogin("login");
            var user2 = repo.GetOrCreateByLogin("login");
            user2.Id.Should().Be(user.Id);
        }

        [Test]
        public void UpdateUser()
        {
            var gameId = Guid.NewGuid();
            var user = repo.GetOrCreateByLogin("login");
            user.CurrentGameId = gameId;
            repo.Update(user);
            var retrieved = repo.FindById(user.Id);
            Assert.NotNull(retrieved);
            retrieved.CurrentGameId.Should().Be(gameId);
        }

        [Test]
        public void GetFirstPage()
        {
            repo.GetOrCreateByLogin("1");
            repo.GetOrCreateByLogin("2");
            repo.GetOrCreateByLogin("3");
            var page = repo.GetPage(1, 2);
            page.CurrentPage.Should().Be(1);
            page.TotalCount.Should().Be(3);
            page.PageSize.Should().Be(2);
            page.Select(u => u.Login).Should().BeEquivalentTo("1", "2");
        }

        [Test]
        public void GetSecondNotFullPage()
        {
            repo.GetOrCreateByLogin("1");
            repo.GetOrCreateByLogin("2");
            repo.GetOrCreateByLogin("3");
            var page = repo.GetPage(2, 2);
            page.CurrentPage.Should().Be(2);
            page.TotalCount.Should().Be(3);
            page.PageSize.Should().Be(2);
            page.Select(u => u.Login).Should().BeEquivalentTo("3");
        }

        [Test]
        public void GetSortedPage()
        {
            repo.GetOrCreateByLogin("2");
            repo.GetOrCreateByLogin("3");
            repo.GetOrCreateByLogin("1");
            var page = repo.GetPage(1, 3);
            page.Select(u => u.Login).Should().Equal("1", "2", "3");
        }

        [Test]
        public void Delete()
        {
            var user = repo.GetOrCreateByLogin("login");
            repo.Delete(user.Id);
            repo.FindById(user.Id).Should().BeNull();
        }

        [Test]
        public void RejectInsertWithExistingId()
        {
            Action action = () => repo.Insert(new UserEntity(Guid.NewGuid()) { Login = "login" });
            action.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void DontInsertMissingUserOnUpdate()
        {
            var user = new UserEntity(Guid.NewGuid()) { Login = "login" };
            repo.Update(user);
            repo.FindById(user.Id).Should().BeNull();
        }

        [Test]
        public void PersistGamesPlayedAfterFinishingGame()
        {
            var user = repo.GetOrCreateByLogin("login");
            user.CurrentGameId = Guid.NewGuid();
            repo.Update(user);

            user = repo.FindById(user.Id);
            user.ExitGame();
            repo.Update(user);

            var retrieved = new MongoUserRepository(TestMongoDatabase.Create()).FindById(user.Id);
            retrieved.GamesPlayed.Should().Be(1);
            retrieved.CurrentGameId.Should().BeNull();
        }

        [Test]
        public void UseUniqueLoginIndex()
        {
            repo.GetOrCreateByLogin("login");
            var db = TestMongoDatabase.Create();
            var collection = db.GetCollection<UserEntity>(MongoUserRepository.CollectionName);
            var index = collection.Indexes.List().ToList()
                .Single(i => i["key"].AsBsonDocument.Contains("Login"));
            index["unique"].AsBoolean.Should().BeTrue();

            var plan = db.RunCommand<BsonDocument>(new BsonDocument("explain",
                new BsonDocument
                {
                    { "find", MongoUserRepository.CollectionName },
                    { "filter", new BsonDocument("Login", "login") }
                }));
            plan["queryPlanner"]["winningPlan"].ToJson().Should().Contain("IXSCAN");
        }


        [Test(Description = "Тест на наличие индекса по логину")]
        [Explicit("Это дополнительная задача Индекс")]
        [MaxTime(15000)]
        public void SearchByLoginFast()
        {
            for (int i = 0; i < 10000; i++)
                repo.GetOrCreateByLogin(i.ToString());
        }


        [Test(Description = "Тест на уникальный индекс по логину")]
        [Explicit("Это дополнительная задача Индекс")]
        public void LoginDuplicateNotAllowed()
        {
            Action action = () =>
            {
                repo.Insert(new UserEntity(Guid.Empty, "somelogin", "last1", "first1", 0, null));
                repo.Insert(new UserEntity(Guid.Empty, "somelogin", "last2", "first2", 0, null));
            };
            action.Should().Throw<MongoWriteException>();
        }

        [Test(Description = "Параллельные запросы не должны падать")]
        [Explicit("Наивная реализация GetOrCreateByLogin не пройдет этот тест")]
        public void MassiveConcurrentCreateUser()
        {
            for (int i = 0; i < 1000; i++)
            {
                var login = "login" + i;
                Task.WaitAll(
                    Task.Run(() => repo.GetOrCreateByLogin(login)),
                    Task.Run(() => repo.GetOrCreateByLogin(login)));
            }
        }
    }
}
