/*M!999999\- enable the sandbox mode */ 
-- MariaDB dump 10.19-11.7.2-MariaDB, for osx10.20 (arm64)
--
-- Host: localhost    Database: demibot
-- ------------------------------------------------------
-- Server version	9.3.0

/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!40101 SET NAMES utf8mb4 */;
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*M!100616 SET @OLD_NOTE_VERBOSITY=@@NOTE_VERBOSITY, NOTE_VERBOSITY=0 */;

--
-- Table structure for table `alembic_version`
--

DROP TABLE IF EXISTS `alembic_version`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `alembic_version` (
  `version_num` varchar(128) NOT NULL,
  PRIMARY KEY (`version_num`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `alembic_version`
--

LOCK TABLES `alembic_version` WRITE;
/*!40000 ALTER TABLE `alembic_version` DISABLE KEYS */;
INSERT INTO `alembic_version` VALUES
('0047_add_role_metadata_fields');
/*!40000 ALTER TABLE `alembic_version` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `appearance_bundle`
--

DROP TABLE IF EXISTS `appearance_bundle`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `appearance_bundle` (
  `id` int NOT NULL AUTO_INCREMENT,
  `fc_id` int DEFAULT NULL,
  `name` varchar(255) NOT NULL,
  `description` text,
  `created_at` datetime NOT NULL,
  `updated_at` datetime NOT NULL,
  PRIMARY KEY (`id`),
  KEY `fc_id` (`fc_id`),
  CONSTRAINT `appearance_bundle_ibfk_1` FOREIGN KEY (`fc_id`) REFERENCES `fc` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `appearance_bundle`
--

LOCK TABLES `appearance_bundle` WRITE;
/*!40000 ALTER TABLE `appearance_bundle` DISABLE KEYS */;
/*!40000 ALTER TABLE `appearance_bundle` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `appearance_bundle_item`
--

DROP TABLE IF EXISTS `appearance_bundle_item`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `appearance_bundle_item` (
  `bundle_id` int NOT NULL,
  `asset_id` int NOT NULL,
  `quantity` int NOT NULL DEFAULT '1',
  PRIMARY KEY (`bundle_id`,`asset_id`),
  KEY `asset_id` (`asset_id`),
  CONSTRAINT `appearance_bundle_item_ibfk_1` FOREIGN KEY (`bundle_id`) REFERENCES `appearance_bundle` (`id`) ON DELETE CASCADE,
  CONSTRAINT `appearance_bundle_item_ibfk_2` FOREIGN KEY (`asset_id`) REFERENCES `asset` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `appearance_bundle_item`
--

LOCK TABLES `appearance_bundle_item` WRITE;
/*!40000 ALTER TABLE `appearance_bundle_item` DISABLE KEYS */;
/*!40000 ALTER TABLE `appearance_bundle_item` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `asset`
--

DROP TABLE IF EXISTS `asset`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `asset` (
  `id` int NOT NULL AUTO_INCREMENT,
  `fc_id` int DEFAULT NULL,
  `kind` enum('appearance','file','script') NOT NULL,
  `name` varchar(255) NOT NULL,
  `hash` varchar(64) NOT NULL,
  `size` int DEFAULT NULL,
  `created_at` datetime NOT NULL,
  `updated_at` datetime NOT NULL,
  `version` int NOT NULL DEFAULT '1',
  `deleted_at` datetime DEFAULT NULL,
  `uploader_id` bigint unsigned DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `ix_asset_hash` (`hash`),
  KEY `ix_asset_fc_id` (`fc_id`),
  KEY `ix_asset_kind` (`kind`),
  KEY `ix_asset_uploader_id` (`uploader_id`),
  CONSTRAINT `asset_ibfk_1` FOREIGN KEY (`fc_id`) REFERENCES `fc` (`id`) ON DELETE CASCADE,
  CONSTRAINT `asset_ibfk_2` FOREIGN KEY (`uploader_id`) REFERENCES `users` (`id`) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `asset`
--

LOCK TABLES `asset` WRITE;
/*!40000 ALTER TABLE `asset` DISABLE KEYS */;
/*!40000 ALTER TABLE `asset` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `asset_dependency`
--

DROP TABLE IF EXISTS `asset_dependency`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `asset_dependency` (
  `asset_id` int NOT NULL,
  `dependency_id` int NOT NULL,
  PRIMARY KEY (`asset_id`,`dependency_id`),
  KEY `dependency_id` (`dependency_id`),
  CONSTRAINT `asset_dependency_ibfk_1` FOREIGN KEY (`asset_id`) REFERENCES `asset` (`id`) ON DELETE CASCADE,
  CONSTRAINT `asset_dependency_ibfk_2` FOREIGN KEY (`dependency_id`) REFERENCES `asset` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `asset_dependency`
--

LOCK TABLES `asset_dependency` WRITE;
/*!40000 ALTER TABLE `asset_dependency` DISABLE KEYS */;
/*!40000 ALTER TABLE `asset_dependency` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `embeds`
--

DROP TABLE IF EXISTS `embeds`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `embeds` (
  `discord_message_id` bigint unsigned NOT NULL,
  `channel_id` bigint unsigned NOT NULL,
  `guild_id` int NOT NULL,
  `payload_json` text NOT NULL,
  `source` varchar(16) NOT NULL,
  `created_at` datetime NOT NULL,
  `updated_at` datetime NOT NULL,
  `buttons_json` text,
  PRIMARY KEY (`discord_message_id`),
  KEY `guild_id` (`guild_id`),
  KEY `ix_embeds_channel_id` (`channel_id`),
  CONSTRAINT `embeds_ibfk_1` FOREIGN KEY (`guild_id`) REFERENCES `guilds` (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `embeds`
--

LOCK TABLES `embeds` WRITE;
/*!40000 ALTER TABLE `embeds` DISABLE KEYS */;
/*!40000 ALTER TABLE `embeds` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `event_buttons`
--

DROP TABLE IF EXISTS `event_buttons`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `event_buttons` (
  `message_id` bigint unsigned NOT NULL,
  `tag` varchar(50) NOT NULL,
  `label` varchar(255) NOT NULL,
  `emoji` varchar(64) DEFAULT NULL,
  `style` int DEFAULT NULL,
  `max_signups` int DEFAULT NULL,
  PRIMARY KEY (`message_id`,`tag`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `event_buttons`
--

LOCK TABLES `event_buttons` WRITE;
/*!40000 ALTER TABLE `event_buttons` DISABLE KEYS */;
/*!40000 ALTER TABLE `event_buttons` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `event_signups`
--

DROP TABLE IF EXISTS `event_signups`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `event_signups` (
  `id` int NOT NULL AUTO_INCREMENT,
  `message_id` bigint unsigned NOT NULL,
  `user_id` bigint unsigned NOT NULL,
  `tag` varchar(50) NOT NULL,
  `created_at` datetime NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uq_event_signups_message_user` (`message_id`,`user_id`),
  KEY `user_id` (`user_id`),
  KEY `ix_event_signups_discord_message_id_choice` (`message_id`,`tag`),
  CONSTRAINT `event_signups_ibfk_1` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `event_signups`
--

LOCK TABLES `event_signups` WRITE;
/*!40000 ALTER TABLE `event_signups` DISABLE KEYS */;
/*!40000 ALTER TABLE `event_signups` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `event_templates`
--

DROP TABLE IF EXISTS `event_templates`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `event_templates` (
  `id` int NOT NULL AUTO_INCREMENT,
  `guild_id` int NOT NULL,
  `name` varchar(255) NOT NULL,
  `description` text,
  `payload_json` text NOT NULL,
  `created_at` datetime NOT NULL,
  `updated_at` datetime NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uq_event_templates_guild_name` (`guild_id`,`name`),
  KEY `ix_event_templates_guild_id` (`guild_id`),
  CONSTRAINT `event_templates_ibfk_1` FOREIGN KEY (`guild_id`) REFERENCES `guilds` (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `event_templates`
--

LOCK TABLES `event_templates` WRITE;
/*!40000 ALTER TABLE `event_templates` DISABLE KEYS */;
/*!40000 ALTER TABLE `event_templates` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `events`
--

DROP TABLE IF EXISTS `events`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `events` (
  `discord_message_id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `channel_id` bigint unsigned NOT NULL,
  `guild_id` int NOT NULL,
  `embeds` json DEFAULT NULL,
  `attachments` json DEFAULT NULL,
  `created_at` datetime NOT NULL,
  PRIMARY KEY (`discord_message_id`),
  KEY `guild_id` (`guild_id`),
  KEY `ix_events_channel_id` (`channel_id`),
  CONSTRAINT `events_ibfk_1` FOREIGN KEY (`guild_id`) REFERENCES `guilds` (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `events`
--

LOCK TABLES `events` WRITE;
/*!40000 ALTER TABLE `events` DISABLE KEYS */;
/*!40000 ALTER TABLE `events` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `fc`
--

DROP TABLE IF EXISTS `fc`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `fc` (
  `id` int NOT NULL AUTO_INCREMENT,
  `name` varchar(255) NOT NULL,
  `world` varchar(32) NOT NULL,
  `created_at` datetime NOT NULL,
  `updated_at` datetime NOT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `fc`
--

LOCK TABLES `fc` WRITE;
/*!40000 ALTER TABLE `fc` DISABLE KEYS */;
/*!40000 ALTER TABLE `fc` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `fc_user`
--

DROP TABLE IF EXISTS `fc_user`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `fc_user` (
  `fc_id` int NOT NULL,
  `user_id` bigint unsigned NOT NULL,
  `joined_at` datetime NOT NULL,
  `settings` text,
  `consent_sync` tinyint(1) NOT NULL DEFAULT '0',
  `last_pull_at` datetime DEFAULT NULL,
  PRIMARY KEY (`fc_id`,`user_id`),
  KEY `fc_user_ibfk_2` (`user_id`),
  CONSTRAINT `fc_user_ibfk_1` FOREIGN KEY (`fc_id`) REFERENCES `fc` (`id`) ON DELETE CASCADE,
  CONSTRAINT `fc_user_ibfk_2` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `fc_user`
--

LOCK TABLES `fc_user` WRITE;
/*!40000 ALTER TABLE `fc_user` DISABLE KEYS */;
/*!40000 ALTER TABLE `fc_user` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `guild_channels`
--

DROP TABLE IF EXISTS `guild_channels`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `guild_channels` (
  `guild_id` int NOT NULL,
  `channel_id` bigint unsigned NOT NULL,
  `kind` enum('chat','event','fc_chat','officer_chat','officer_visible') NOT NULL,
  `name` varchar(255) DEFAULT NULL,
  `webhook_url` varchar(255) DEFAULT NULL,
  PRIMARY KEY (`guild_id`,`channel_id`,`kind`),
  UNIQUE KEY `uq_guild_channels_guild_channel` (`guild_id`,`channel_id`),
  CONSTRAINT `guild_channels_ibfk_1` FOREIGN KEY (`guild_id`) REFERENCES `guilds` (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `guild_channels`
--

LOCK TABLES `guild_channels` WRITE;
/*!40000 ALTER TABLE `guild_channels` DISABLE KEYS */;
INSERT INTO `guild_channels` VALUES
(1,1181350937634283562,'chat','Text Channels',NULL),
(1,1181350937634283563,'chat','Voice Channels',NULL),
(1,1181350937634283564,'chat','general',NULL),
(1,1181621576609845268,'chat','events',NULL),
(1,1192200665049616524,'chat','test',NULL),
(1,1219051340030808094,'chat','game-dev',NULL),
(1,1232552963604615208,'chat','housing',NULL),
(1,1355530969309909003,'chat','streamchannel',NULL),
(1,1406428279874781314,'chat','📜justice-archon-rules📜',NULL),
(1,1406428526168637600,'chat','Administration',NULL),
(1,1406428657748021308,'chat','🙀super-cereal🙀',NULL),
(1,1406428689780047872,'chat','🛡️modlog🛡️',NULL),
(1,1406428761171300516,'chat','📦crafting-list📦',NULL),
(1,1406428798559453284,'chat','🎭shenanigans🎭',NULL),
(1,1406645986985644143,'chat','demibot-test',NULL),
(1,1411094717969600626,'chat','vault',NULL),
(1,1411127020146000004,'chat','test2',NULL),
(3,1218700195169304688,'chat','Development Channels',NULL),
(3,1218700195169304689,'chat','Voice Channels',NULL),
(3,1218700195169304690,'chat','general',NULL),
(3,1218700195169304691,'chat','General',NULL),
(3,1223823784440762459,'chat','app-development',NULL),
(3,1223823882918826095,'chat','demi-bot-development',NULL),
(3,1223830267643301938,'chat','bot-test',NULL),
(3,1224148951255482418,'chat','screenshots',NULL),
(3,1224149448972701767,'chat','demi-os-updates',NULL),
(3,1260284823734587392,'chat','shaders',NULL),
(3,1260286454983032962,'chat','animations',NULL),
(3,1260459924333269023,'chat','fantabode-mod-development',NULL),
(3,1308044105716203560,'chat','event-test',NULL),
(3,1330890312884818001,'chat','Test Server',NULL),
(3,1330890449271132161,'chat','raid-events',NULL),
(3,1330890501498601604,'chat','social-events',NULL),
(3,1330890641651142799,'chat','treasure-map-events',NULL),
(3,1330890752804257792,'chat','deep-dungeon-events',NULL),
(3,1330890816092246048,'chat','contests',NULL),
(3,1330891011731488778,'chat','bozja-eureka-events',NULL),
(3,1330891039791255603,'chat','pvp-events',NULL),
(3,1330891793146974218,'chat','miscellaneous-events',NULL),
(3,1411094720884641814,'chat','vault',NULL),
(4,1337786583696408616,'chat','General',NULL),
(4,1337786583696408617,'chat','Voice Channels',NULL),
(4,1337786583696408618,'fc_chat','💬general-discussion',NULL),
(4,1337786583696408619,'chat','Developer General',NULL),
(4,1337790097398960221,'chat','introduction',NULL),
(4,1337790258762354860,'chat','DemiOS',NULL),
(4,1337790287895990392,'chat','DemiBot',NULL),
(4,1337790374693048381,'chat','💬demibot-general',NULL),
(4,1337790412659752960,'chat','💬demios-general',NULL),
(4,1337790741573009419,'chat','⌘commands⎋',NULL),
(4,1337791002596872192,'chat','development-discussion',NULL),
(4,1337791050294755380,'chat','screenshots',NULL),
(4,1337791314531713114,'chat','features',NULL),
(4,1337791355287633981,'chat','development-discussion',NULL),
(4,1337791385331564576,'chat','screenshots',NULL),
(4,1337791435705024543,'chat','unity-development',NULL),
(4,1337791613065494621,'chat','server-development',NULL),
(4,1337791742820483143,'chat','Dev Unit Website',NULL),
(4,1337791793370107934,'chat','💬website-general',NULL),
(4,1337791831747985448,'chat','development-updates',NULL),
(4,1337791861724545054,'chat','screenshots',NULL),
(4,1337792558465552384,'chat','Discord AdventureBot',NULL),
(4,1337792628963414117,'chat','💬adventure-general',NULL),
(4,1337792668368896060,'chat','🛠️development-discussion',NULL),
(4,1337792714636267642,'chat','📺screenshots',NULL),
(4,1337793329689264221,'chat','FFXIV Plugins',NULL),
(4,1337851788480741478,'chat','demibot-test-channel',NULL),
(4,1337852614984994816,'officer_chat','🧬static-free-zone🧬',NULL),
(4,1337852935014715413,'chat','🎨concept-art',NULL),
(4,1338115982887227392,'chat','concepts',NULL),
(4,1339959772853567541,'chat','related-projects',NULL),
(4,1355942957269913600,'chat','3-0-2-patch-notes',NULL),
(4,1358191501275955381,'chat','📜patch-3-1-0-development',NULL),
(4,1358522165657468998,'chat','3-0-3-patch-notes',NULL),
(4,1360081757034250301,'chat','3-0-4-patch-notes',NULL),
(4,1360683660365529300,'chat','🐞bug-reports',NULL),
(4,1361455684427845953,'chat','3-0-5-patch-notes',NULL),
(4,1362832151485354065,'chat','assets',NULL),
(4,1362881397672509471,'chat','💻-codebase',NULL),
(4,1362895625884270874,'chat','3-0-6-patch-notes',NULL),
(4,1363671994951925955,'chat','adventurebot',NULL),
(4,1366484852244746482,'chat','3-0-7-patch-notes',NULL),
(4,1371280451259334736,'chat','3-0-8-patch-notes',NULL),
(4,1376040430445133895,'chat','📜3-0-9-patch-notes',NULL),
(4,1386129365036961842,'chat','📦 Archive',NULL),
(4,1386135363436937277,'chat','🧙‍♂️adventure-bot-2',NULL),
(4,1386362392375590963,'chat','HelperBot',NULL),
(4,1386362550287077476,'chat','general',NULL),
(4,1386362635494232074,'chat','helper-bot-test',NULL),
(4,1388334322926223521,'chat','AdventureGame',NULL),
(4,1388340433670901830,'chat','development-discussion',NULL),
(4,1388340644107260064,'chat','development-updates',NULL),
(4,1388340757995192331,'chat','release-notes',NULL),
(4,1388340811527356617,'chat','screenshots',NULL),
(4,1388571404764577894,'chat','resources',NULL),
(4,1399028527126286376,'chat','🌀Project Realm',NULL),
(4,1399028899748511835,'chat','general-discussion',NULL),
(4,1399029035496902818,'chat','development-discussion',NULL),
(4,1399029125179641986,'chat','unity-discussion',NULL),
(4,1399029217580023838,'chat','concept-art',NULL),
(4,1399029251759538348,'chat','screenshots',NULL),
(4,1399029549752123445,'chat','features',NULL),
(4,1399029756216868924,'chat','🧠SoulMind',NULL),
(4,1399029913779966083,'chat','break-room',NULL),
(4,1399030000501526620,'chat','☕coffee',NULL),
(4,1399030297206456380,'chat','🧬static-free-zone',NULL),
(4,1399030384662155335,'chat','the-clipboard',NULL),
(4,1403886348187734109,'chat','🧩fantabode',NULL),
(4,1406838711697674371,'chat','🧩demicat-info🧩',NULL),
(4,1408425776415510639,'chat','🧩mare-service🧩',NULL),
(4,1408599011056812052,'chat','DemiMare',NULL),
(4,1408599383280320663,'chat','commands',NULL),
(4,1408599470538490008,'chat','bot-log',NULL),
(4,1408924101187211316,'chat','🧩mare-service-debug🧩',NULL),
(4,1409911507348619264,'chat','DemiCat',NULL),
(4,1409911863386575002,'chat','📜demicat-v1-2-2-1-release-notes📜',NULL),
(4,1411094725305303194,'chat','vault',NULL),
(4,1411324235472830598,'chat','concepts',NULL),
(4,1412094196332101844,'chat','📺screenshots',NULL),
(4,1414766174969008199,'chat','🧠brain-storms⛈️',NULL),
(4,1415662125023494184,'chat','📦official-releases📦',NULL),
(4,1417130101316784178,'fc_chat','fc-chat-test',NULL),
(4,1417130190995197982,'event','events-test',NULL),
(4,1418749302196404224,'officer_chat','officer-chat-test',NULL),
(4,1418945980765700177,'chat','setup-guide',NULL);
/*!40000 ALTER TABLE `guild_channels` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `guild_config`
--

DROP TABLE IF EXISTS `guild_config`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `guild_config` (
  `id` int NOT NULL AUTO_INCREMENT,
  `guild_id` int DEFAULT NULL,
  `officer_visible_channel_id` bigint unsigned DEFAULT NULL,
  `officer_role_ids` text,
  `mention_role_ids` text,
  PRIMARY KEY (`id`),
  KEY `guild_id` (`guild_id`),
  CONSTRAINT `guild_config_ibfk_1` FOREIGN KEY (`guild_id`) REFERENCES `guilds` (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `guild_config`
--

LOCK TABLES `guild_config` WRITE;
/*!40000 ALTER TABLE `guild_config` DISABLE KEYS */;
INSERT INTO `guild_config` VALUES
(1,4,NULL,'1337814848322666546','1370879778344931371,1370878413111431248,1337814848322666546');
/*!40000 ALTER TABLE `guild_config` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `guilds`
--

DROP TABLE IF EXISTS `guilds`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `guilds` (
  `id` int NOT NULL AUTO_INCREMENT,
  `discord_guild_id` bigint unsigned NOT NULL,
  `name` varchar(255) NOT NULL,
  `created_at` datetime NOT NULL,
  `updated_at` datetime NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `ix_guilds_discord_guild_id` (`discord_guild_id`)
) ENGINE=InnoDB AUTO_INCREMENT=5 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `guilds`
--

LOCK TABLES `guilds` WRITE;
/*!40000 ALTER TABLE `guilds` DISABLE KEYS */;
INSERT INTO `guilds` VALUES
(1,1181350937634283561,'Ultimateria','2025-09-25 11:12:16','2025-09-25 11:12:16'),
(3,1218700194489700483,'DemiBot','2025-09-25 11:12:16','2025-09-25 11:12:16'),
(4,1337786582996095036,'Demi Dev Unit','2025-09-25 11:12:16','2025-09-25 11:12:16');
/*!40000 ALTER TABLE `guilds` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `index_checkpoint`
--

DROP TABLE IF EXISTS `index_checkpoint`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `index_checkpoint` (
  `id` int NOT NULL AUTO_INCREMENT,
  `kind` enum('appearance','file','script') NOT NULL,
  `last_id` int NOT NULL,
  `last_generated_at` datetime NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `ix_index_checkpoint_kind` (`kind`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `index_checkpoint`
--

LOCK TABLES `index_checkpoint` WRITE;
/*!40000 ALTER TABLE `index_checkpoint` DISABLE KEYS */;
/*!40000 ALTER TABLE `index_checkpoint` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `membership_roles`
--

DROP TABLE IF EXISTS `membership_roles`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `membership_roles` (
  `membership_id` int NOT NULL,
  `role_id` int NOT NULL,
  PRIMARY KEY (`membership_id`,`role_id`),
  KEY `role_id` (`role_id`),
  CONSTRAINT `membership_roles_ibfk_1` FOREIGN KEY (`membership_id`) REFERENCES `memberships` (`id`),
  CONSTRAINT `membership_roles_ibfk_2` FOREIGN KEY (`role_id`) REFERENCES `roles` (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `membership_roles`
--

LOCK TABLES `membership_roles` WRITE;
/*!40000 ALTER TABLE `membership_roles` DISABLE KEYS */;
INSERT INTO `membership_roles` VALUES
(3,1),
(4,2),
(5,3),
(6,4),
(7,4),
(8,4),
(11,5),
(12,6),
(14,6),
(15,6),
(16,6),
(17,6),
(18,6),
(19,6),
(20,6),
(21,6),
(22,6),
(23,6),
(24,6),
(13,7),
(15,8),
(27,8),
(23,9),
(25,10),
(26,11),
(28,12);
/*!40000 ALTER TABLE `membership_roles` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `memberships`
--

DROP TABLE IF EXISTS `memberships`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `memberships` (
  `id` int NOT NULL AUTO_INCREMENT,
  `guild_id` int NOT NULL,
  `user_id` bigint unsigned NOT NULL,
  `nickname` varchar(255) DEFAULT NULL,
  `avatar_url` varchar(255) DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uq_memberships_guild_user` (`guild_id`,`user_id`),
  KEY `memberships_ibfk_2` (`user_id`),
  CONSTRAINT `memberships_ibfk_1` FOREIGN KEY (`guild_id`) REFERENCES `guilds` (`id`),
  CONSTRAINT `memberships_ibfk_2` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=29 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `memberships`
--

LOCK TABLES `memberships` WRITE;
/*!40000 ALTER TABLE `memberships` DISABLE KEYS */;
INSERT INTO `memberships` VALUES
(1,1,1,'thee oracle','https://cdn.discordapp.com/avatars/87046372833910784/3ee0a948afdca80c8cfadafb8cd3d64b.png?size=1024'),
(2,1,2,'Asaki Stormfall','https://cdn.discordapp.com/avatars/331920838221758464/69be3ed68fce62b81f94f9058fc67a09.png?size=1024'),
(3,1,3,'Apollo','https://cdn.discordapp.com/avatars/475744554910351370/a8cd99a5b1feadceef2c2fba4bd64c5f.png?size=1024'),
(4,1,4,'Blink','https://cdn.discordapp.com/guilds/1181350937634283561/users/821995310179155999/avatars/dad92a5f556bb1172e0a71b8a52dc64e.png?size=1024'),
(5,1,5,'Demi Bot','https://cdn.discordapp.com/avatars/1307692839504838807/45da3529b240a708954ebd32f4ecc38b.png?size=1024'),
(6,3,1,'thee oracle','https://cdn.discordapp.com/avatars/87046372833910784/3ee0a948afdca80c8cfadafb8cd3d64b.png?size=1024'),
(7,3,6,'Snow Hey Oh','https://cdn.discordapp.com/avatars/248324194692104194/d6288cde72e98d5b6890386e1d2d693c.png?size=1024'),
(8,3,7,'Sophia Pistis (Any)','https://cdn.discordapp.com/avatars/480510060796313630/6f22782aae0b13da97a2a6369a1aa9c5.png?size=1024'),
(9,3,8,'RxDrStocks','https://cdn.discordapp.com/avatars/788245484540002335/e3dfcaa8278552a182681d9829e3254d.png?size=1024'),
(10,3,4,'Blink','https://cdn.discordapp.com/avatars/821995310179155999/1ad9c37b2cf49c6df3905d8c84d424d3.png?size=1024'),
(11,3,5,'Demi Bot','https://cdn.discordapp.com/avatars/1307692839504838807/45da3529b240a708954ebd32f4ecc38b.png?size=1024'),
(12,4,1,'thee oracle','https://cdn.discordapp.com/avatars/87046372833910784/3ee0a948afdca80c8cfadafb8cd3d64b.png?size=1024'),
(13,4,9,'Murder-Bot','https://cdn.discordapp.com/avatars/235148962103951360/ed3dac3b6e7a851df781632a4295fcb9.png?size=1024'),
(14,4,6,'Snow Hey Oh','https://cdn.discordapp.com/avatars/248324194692104194/d6288cde72e98d5b6890386e1d2d693c.png?size=1024'),
(15,4,10,'Yeyito','https://cdn.discordapp.com/avatars/272824833346240512/b20705325434b79ba8547af571d20855.png?size=1024'),
(16,4,11,'Oni Neko-Punch!','https://cdn.discordapp.com/avatars/288156980009238529/b791d8bf62fdedaf7af587c2a2f00feb.png?size=1024'),
(17,4,12,'Sarah Lucid','https://cdn.discordapp.com/avatars/344822496022757379/b37190252f6d8451e26c0c00e8929c46.png?size=1024'),
(18,4,13,'rosae','https://cdn.discordapp.com/avatars/349251322610057216/c10b09520f9ce00747afd2b8da142b1b.png?size=1024'),
(19,4,7,'Sophia Pistis (The Eepiest)','https://cdn.discordapp.com/avatars/480510060796313630/6f22782aae0b13da97a2a6369a1aa9c5.png?size=1024'),
(20,4,14,'Marco A.','https://cdn.discordapp.com/avatars/688159443317424149/3b5bbf041b9ff423f538b076b4903b40.png?size=1024'),
(21,4,15,'Jazrin','https://cdn.discordapp.com/embed/avatars/0.png'),
(22,4,8,'RxDrStocks','https://cdn.discordapp.com/avatars/788245484540002335/e3dfcaa8278552a182681d9829e3254d.png?size=1024'),
(23,4,4,'Blink','https://cdn.discordapp.com/guilds/1337786582996095036/users/821995310179155999/avatars/3ee11f34dc95933704b2de59b10d8cea.png?size=1024'),
(24,4,16,'Liehn','https://cdn.discordapp.com/avatars/833812630615359498/e9a3b179864bd0d7611dec1cc663afb5.png?size=1024'),
(25,4,5,'Demi Bot','https://cdn.discordapp.com/avatars/1307692839504838807/45da3529b240a708954ebd32f4ecc38b.png?size=1024'),
(26,4,17,'AdventureBot','https://cdn.discordapp.com/avatars/1348488328629977153/59cbdf4598cd8edd2111ee0a9cae4468.png?size=1024'),
(27,4,18,'Bianka81','https://cdn.discordapp.com/avatars/1358112153152000022/5cf6af65f92779e7235f4b4a9bed36ba.png?size=1024'),
(28,4,19,'HelperBot','https://cdn.discordapp.com/embed/avatars/1.png');
/*!40000 ALTER TABLE `memberships` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `messages`
--

DROP TABLE IF EXISTS `messages`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `messages` (
  `discord_message_id` bigint unsigned NOT NULL,
  `channel_id` bigint unsigned NOT NULL,
  `guild_id` int NOT NULL,
  `author_id` bigint unsigned NOT NULL,
  `author_name` varchar(255) NOT NULL,
  `content_raw` text NOT NULL,
  `content_display` text NOT NULL,
  `is_officer` tinyint(1) NOT NULL DEFAULT '0',
  `created_at` datetime NOT NULL,
  `author_avatar_url` varchar(255) DEFAULT NULL,
  `attachments_json` text,
  `content` text,
  `author_json` text,
  `embeds_json` text,
  `mentions_json` text,
  `reference_json` text,
  `components_json` text,
  `edited_timestamp` datetime DEFAULT NULL,
  `reactions_json` text,
  PRIMARY KEY (`discord_message_id`),
  KEY `guild_id` (`guild_id`),
  KEY `ix_messages_channel_id_created_at` (`channel_id`,`created_at`),
  KEY `messages_ibfk_2` (`author_id`),
  CONSTRAINT `messages_ibfk_1` FOREIGN KEY (`guild_id`) REFERENCES `guilds` (`id`),
  CONSTRAINT `messages_ibfk_2` FOREIGN KEY (`author_id`) REFERENCES `users` (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `messages`
--

LOCK TABLES `messages` WRITE;
/*!40000 ALTER TABLE `messages` DISABLE KEYS */;
/*!40000 ALTER TABLE `messages` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `posted_messages`
--

DROP TABLE IF EXISTS `posted_messages`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `posted_messages` (
  `id` int NOT NULL AUTO_INCREMENT,
  `guild_id` int NOT NULL,
  `channel_id` bigint unsigned NOT NULL,
  `local_message_id` bigint unsigned NOT NULL,
  `discord_message_id` bigint unsigned NOT NULL,
  `webhook_url` varchar(255) DEFAULT NULL,
  `embed_json` text,
  `nonce` varchar(64) DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uq_posted_messages_guild_local` (`guild_id`,`local_message_id`),
  UNIQUE KEY `uq_posted_messages_discord` (`discord_message_id`),
  UNIQUE KEY `uq_posted_messages_guild_channel_nonce` (`guild_id`,`channel_id`,`nonce`),
  KEY `ix_posted_messages_guild_channel` (`guild_id`,`channel_id`),
  CONSTRAINT `posted_messages_ibfk_1` FOREIGN KEY (`guild_id`) REFERENCES `guilds` (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `posted_messages`
--

LOCK TABLES `posted_messages` WRITE;
/*!40000 ALTER TABLE `posted_messages` DISABLE KEYS */;
/*!40000 ALTER TABLE `posted_messages` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `presences`
--

DROP TABLE IF EXISTS `presences`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `presences` (
  `guild_id` bigint unsigned NOT NULL,
  `user_id` bigint unsigned NOT NULL,
  `status` varchar(16) NOT NULL,
  `updated_at` datetime NOT NULL,
  `status_text` varchar(128) DEFAULT NULL,
  PRIMARY KEY (`guild_id`,`user_id`),
  KEY `ix_presences_guild_id_status` (`guild_id`,`status`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `presences`
--

LOCK TABLES `presences` WRITE;
/*!40000 ALTER TABLE `presences` DISABLE KEYS */;
INSERT INTO `presences` VALUES
(1,87046372833910784,'offline','2025-09-25 11:17:33',NULL),
(1,331920838221758464,'offline','2025-09-25 11:17:33',NULL),
(1,475744554910351370,'online','2025-09-25 11:17:33',NULL),
(1,821995310179155999,'online','2025-09-25 11:18:09',NULL),
(1,1307692839504838807,'online','2025-09-25 11:17:33',NULL),
(3,87046372833910784,'offline','2025-09-25 11:17:33',NULL),
(3,248324194692104194,'offline','2025-09-25 11:17:33',NULL),
(3,480510060796313630,'offline','2025-09-25 11:33:10',NULL),
(3,788245484540002335,'offline','2025-09-25 11:17:33',NULL),
(3,821995310179155999,'online','2025-09-25 11:18:09',NULL),
(3,1307692839504838807,'online','2025-09-25 11:17:33',NULL),
(4,87046372833910784,'offline','2025-09-25 11:17:33',NULL),
(4,235148962103951360,'online','2025-09-25 11:17:33',NULL),
(4,248324194692104194,'offline','2025-09-25 11:17:33',NULL),
(4,272824833346240512,'offline','2025-09-25 11:17:33',NULL),
(4,288156980009238529,'offline','2025-09-25 11:17:33',NULL),
(4,344822496022757379,'offline','2025-09-25 11:17:33',NULL),
(4,349251322610057216,'idle','2025-09-25 11:17:33',NULL),
(4,480510060796313630,'offline','2025-09-25 11:33:10',NULL),
(4,688159443317424149,'offline','2025-09-25 11:17:33',NULL),
(4,738246485141094530,'offline','2025-09-25 11:17:33',NULL),
(4,788245484540002335,'offline','2025-09-25 11:17:33',NULL),
(4,821995310179155999,'online','2025-09-25 11:18:09',NULL),
(4,833812630615359498,'offline','2025-09-25 11:17:33',NULL),
(4,1307692839504838807,'online','2025-09-25 11:17:33',NULL),
(4,1348488328629977153,'offline','2025-09-25 11:17:33',NULL),
(4,1358112153152000022,'offline','2025-09-25 11:17:33',NULL),
(4,1386358170934575285,'offline','2025-09-25 11:17:33',NULL);
/*!40000 ALTER TABLE `presences` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `recurring_events`
--

DROP TABLE IF EXISTS `recurring_events`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `recurring_events` (
  `id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `guild_id` int NOT NULL,
  `channel_id` bigint unsigned NOT NULL,
  `repeat` varchar(16) NOT NULL,
  `next_post_at` datetime NOT NULL,
  `payload_json` text NOT NULL,
  PRIMARY KEY (`id`),
  KEY `guild_id` (`guild_id`),
  CONSTRAINT `recurring_events_ibfk_1` FOREIGN KEY (`guild_id`) REFERENCES `guilds` (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `recurring_events`
--

LOCK TABLES `recurring_events` WRITE;
/*!40000 ALTER TABLE `recurring_events` DISABLE KEYS */;
/*!40000 ALTER TABLE `recurring_events` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `request_events`
--

DROP TABLE IF EXISTS `request_events`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `request_events` (
  `id` int NOT NULL AUTO_INCREMENT,
  `request_id` int NOT NULL,
  `event_id` bigint NOT NULL,
  `created_at` datetime NOT NULL,
  `updated_at` datetime NOT NULL,
  `version` int NOT NULL DEFAULT '1',
  PRIMARY KEY (`id`),
  KEY `ix_request_events_request_id` (`request_id`),
  CONSTRAINT `request_events_ibfk_1` FOREIGN KEY (`request_id`) REFERENCES `requests` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `request_events`
--

LOCK TABLES `request_events` WRITE;
/*!40000 ALTER TABLE `request_events` DISABLE KEYS */;
/*!40000 ALTER TABLE `request_events` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `request_items`
--

DROP TABLE IF EXISTS `request_items`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `request_items` (
  `id` int NOT NULL AUTO_INCREMENT,
  `request_id` int NOT NULL,
  `item_id` bigint NOT NULL,
  `quantity` int NOT NULL DEFAULT '1',
  `created_at` datetime NOT NULL,
  `updated_at` datetime NOT NULL,
  `version` int NOT NULL DEFAULT '1',
  `hq` tinyint(1) NOT NULL DEFAULT '0',
  PRIMARY KEY (`id`),
  KEY `ix_request_items_request_id` (`request_id`),
  CONSTRAINT `request_items_ibfk_1` FOREIGN KEY (`request_id`) REFERENCES `requests` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `request_items`
--

LOCK TABLES `request_items` WRITE;
/*!40000 ALTER TABLE `request_items` DISABLE KEYS */;
/*!40000 ALTER TABLE `request_items` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `request_runs`
--

DROP TABLE IF EXISTS `request_runs`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `request_runs` (
  `id` int NOT NULL AUTO_INCREMENT,
  `request_id` int NOT NULL,
  `run_id` bigint NOT NULL,
  `created_at` datetime NOT NULL,
  `updated_at` datetime NOT NULL,
  `version` int NOT NULL DEFAULT '1',
  PRIMARY KEY (`id`),
  KEY `ix_request_runs_request_id` (`request_id`),
  CONSTRAINT `request_runs_ibfk_1` FOREIGN KEY (`request_id`) REFERENCES `requests` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `request_runs`
--

LOCK TABLES `request_runs` WRITE;
/*!40000 ALTER TABLE `request_runs` DISABLE KEYS */;
/*!40000 ALTER TABLE `request_runs` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `request_tombstones`
--

DROP TABLE IF EXISTS `request_tombstones`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `request_tombstones` (
  `request_id` bigint unsigned NOT NULL,
  `guild_id` int NOT NULL,
  `version` int NOT NULL,
  `deleted_at` datetime DEFAULT NULL,
  PRIMARY KEY (`request_id`),
  KEY `ix_request_tombstones_deleted_at` (`deleted_at`),
  KEY `ix_request_tombstones_guild_id` (`guild_id`),
  CONSTRAINT `request_tombstones_ibfk_1` FOREIGN KEY (`guild_id`) REFERENCES `guilds` (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `request_tombstones`
--

LOCK TABLES `request_tombstones` WRITE;
/*!40000 ALTER TABLE `request_tombstones` DISABLE KEYS */;
/*!40000 ALTER TABLE `request_tombstones` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `requests`
--

DROP TABLE IF EXISTS `requests`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `requests` (
  `id` int NOT NULL AUTO_INCREMENT,
  `guild_id` int NOT NULL,
  `user_id` bigint unsigned NOT NULL,
  `title` varchar(255) NOT NULL,
  `description` text,
  `type` enum('item','run','event') NOT NULL,
  `status` enum('open','claimed','in_progress','awaiting_confirm','completed','cancelled','approved','denied') NOT NULL,
  `urgency` enum('low','medium','high') NOT NULL,
  `created_at` datetime NOT NULL,
  `updated_at` datetime NOT NULL,
  `version` int NOT NULL DEFAULT '1',
  `assignee_id` bigint unsigned DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `guild_id` (`guild_id`),
  KEY `ix_requests_type` (`type`),
  KEY `ix_requests_status` (`status`),
  KEY `ix_requests_urgency` (`urgency`),
  KEY `ix_requests_assignee_id` (`assignee_id`),
  KEY `requests_ibfk_2` (`user_id`),
  FULLTEXT KEY `ix_requests_text` (`title`,`description`),
  CONSTRAINT `requests_ibfk_1` FOREIGN KEY (`guild_id`) REFERENCES `guilds` (`id`),
  CONSTRAINT `requests_ibfk_2` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`),
  CONSTRAINT `requests_ibfk_3` FOREIGN KEY (`assignee_id`) REFERENCES `users` (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `requests`
--

LOCK TABLES `requests` WRITE;
/*!40000 ALTER TABLE `requests` DISABLE KEYS */;
/*!40000 ALTER TABLE `requests` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `roles`
--

DROP TABLE IF EXISTS `roles`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `roles` (
  `id` int NOT NULL AUTO_INCREMENT,
  `guild_id` int NOT NULL,
  `name` varchar(255) NOT NULL,
  `is_officer` tinyint(1) NOT NULL DEFAULT '0',
  `is_chat` tinyint(1) NOT NULL DEFAULT '0',
  `discord_role_id` bigint unsigned NOT NULL,
  `position` int NOT NULL DEFAULT '0',
  `hoist` tinyint(1) NOT NULL DEFAULT '0',
  `premium_subscriber` tinyint(1) NOT NULL DEFAULT '0',
  PRIMARY KEY (`id`),
  UNIQUE KEY `ix_roles_discord_role_id` (`discord_role_id`),
  KEY `guild_id` (`guild_id`),
  CONSTRAINT `roles_ibfk_1` FOREIGN KEY (`guild_id`) REFERENCES `guilds` (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=14 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `roles`
--

LOCK TABLES `roles` WRITE;
/*!40000 ALTER TABLE `roles` DISABLE KEYS */;
INSERT INTO `roles` VALUES
(1,1,'Apollo',0,0,1181414198803169342,3,0,0),
(2,1,'Officer',0,0,1181620572770291712,2,0,0),
(3,1,'Demi Bot',0,0,1406655812155347080,1,0,0),
(4,3,'Moderator',0,0,1307767107269824563,1,0,0),
(5,3,'Demi Bot',0,0,1307718353628041296,2,0,0),
(6,4,'Developer',1,1,1337814848322666546,8,1,0),
(7,4,'carl-bot',0,0,1370880959267737743,3,0,0),
(8,4,'Adventurer',0,1,1370878413111431248,5,0,0),
(9,4,'Server Booster',0,0,1337883328908497028,7,0,1),
(10,4,'Demi Bot',0,0,1414258633579171840,1,0,0),
(11,4,'AdventureBot',0,0,1358532059009781924,6,0,0),
(12,4,'HelperBot',0,0,1386360157016948901,2,0,0),
(13,4,'Mechanical Catto',0,1,1370879778344931371,4,0,0);
/*!40000 ALTER TABLE `roles` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `signup_presets`
--

DROP TABLE IF EXISTS `signup_presets`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `signup_presets` (
  `id` int NOT NULL AUTO_INCREMENT,
  `guild_id` int NOT NULL,
  `name` varchar(255) NOT NULL,
  `buttons_json` text NOT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_signup_presets_guild_id` (`guild_id`),
  CONSTRAINT `signup_presets_ibfk_1` FOREIGN KEY (`guild_id`) REFERENCES `guilds` (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `signup_presets`
--

LOCK TABLES `signup_presets` WRITE;
/*!40000 ALTER TABLE `signup_presets` DISABLE KEYS */;
/*!40000 ALTER TABLE `signup_presets` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `syncshell_manifests`
--

DROP TABLE IF EXISTS `syncshell_manifests`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `syncshell_manifests` (
  `user_id` bigint unsigned NOT NULL,
  `manifest_json` text NOT NULL,
  `created_at` datetime NOT NULL,
  `updated_at` datetime NOT NULL,
  PRIMARY KEY (`user_id`),
  CONSTRAINT `syncshell_manifests_ibfk_1` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `syncshell_manifests`
--

LOCK TABLES `syncshell_manifests` WRITE;
/*!40000 ALTER TABLE `syncshell_manifests` DISABLE KEYS */;
/*!40000 ALTER TABLE `syncshell_manifests` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `syncshell_pairings`
--

DROP TABLE IF EXISTS `syncshell_pairings`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `syncshell_pairings` (
  `user_id` bigint unsigned NOT NULL,
  `token` varchar(64) NOT NULL,
  `created_at` datetime NOT NULL,
  `expires_at` datetime NOT NULL,
  PRIMARY KEY (`user_id`),
  UNIQUE KEY `ix_syncshell_pairings_token` (`token`),
  KEY `ix_syncshell_pairings_expires_at` (`expires_at`),
  CONSTRAINT `syncshell_pairings_ibfk_1` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `syncshell_pairings`
--

LOCK TABLES `syncshell_pairings` WRITE;
/*!40000 ALTER TABLE `syncshell_pairings` DISABLE KEYS */;
/*!40000 ALTER TABLE `syncshell_pairings` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `syncshell_rate_limits`
--

DROP TABLE IF EXISTS `syncshell_rate_limits`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `syncshell_rate_limits` (
  `user_id` bigint unsigned NOT NULL,
  `requests` int NOT NULL DEFAULT '0',
  `window_start` datetime NOT NULL,
  PRIMARY KEY (`user_id`),
  CONSTRAINT `syncshell_rate_limits_ibfk_1` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `syncshell_rate_limits`
--

LOCK TABLES `syncshell_rate_limits` WRITE;
/*!40000 ALTER TABLE `syncshell_rate_limits` DISABLE KEYS */;
/*!40000 ALTER TABLE `syncshell_rate_limits` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `unicode_emojis`
--

DROP TABLE IF EXISTS `unicode_emojis`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `unicode_emojis` (
  `emoji` varchar(16) NOT NULL,
  `name` varchar(255) NOT NULL,
  `image_url` varchar(255) NOT NULL,
  PRIMARY KEY (`emoji`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `unicode_emojis`
--

LOCK TABLES `unicode_emojis` WRITE;
/*!40000 ALTER TABLE `unicode_emojis` DISABLE KEYS */;
/*!40000 ALTER TABLE `unicode_emojis` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `user_installation`
--

DROP TABLE IF EXISTS `user_installation`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `user_installation` (
  `id` int NOT NULL AUTO_INCREMENT,
  `user_id` bigint unsigned NOT NULL,
  `asset_id` int NOT NULL,
  `status` enum('DOWNLOADED','INSTALLED','APPLIED','FAILED') NOT NULL,
  `asset_hash` varchar(64) DEFAULT NULL,
  `created_at` datetime NOT NULL,
  `updated_at` datetime NOT NULL,
  `version` int NOT NULL DEFAULT '1',
  PRIMARY KEY (`id`),
  KEY `ix_user_installation_user_id` (`user_id`),
  KEY `ix_user_installation_asset_id` (`asset_id`),
  KEY `ix_user_installation_status` (`status`),
  CONSTRAINT `user_installation_ibfk_1` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE,
  CONSTRAINT `user_installation_ibfk_2` FOREIGN KEY (`asset_id`) REFERENCES `asset` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `user_installation`
--

LOCK TABLES `user_installation` WRITE;
/*!40000 ALTER TABLE `user_installation` DISABLE KEYS */;
/*!40000 ALTER TABLE `user_installation` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `user_keys`
--

DROP TABLE IF EXISTS `user_keys`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `user_keys` (
  `id` int NOT NULL AUTO_INCREMENT,
  `user_id` bigint unsigned NOT NULL,
  `guild_id` int NOT NULL,
  `token` varchar(64) NOT NULL,
  `enabled` tinyint(1) NOT NULL DEFAULT '1',
  `roles_cached` text,
  `created_at` datetime NOT NULL,
  `updated_at` datetime NOT NULL,
  `last_used_at` datetime DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `ix_user_keys_token` (`token`),
  KEY `guild_id` (`guild_id`),
  KEY `user_keys_ibfk_1` (`user_id`),
  CONSTRAINT `user_keys_ibfk_1` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`),
  CONSTRAINT `user_keys_ibfk_2` FOREIGN KEY (`guild_id`) REFERENCES `guilds` (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `user_keys`
--

LOCK TABLES `user_keys` WRITE;
/*!40000 ALTER TABLE `user_keys` DISABLE KEYS */;
INSERT INTO `user_keys` VALUES
(1,4,4,'aacd82b3d41b1ccf974ea2a1ddffb3dd',1,'1337883328908497028,1337814848322666546','2025-09-25 11:20:14','2025-09-25 11:20:14',NULL);
/*!40000 ALTER TABLE `user_keys` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `users`
--

DROP TABLE IF EXISTS `users`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8mb4 */;
CREATE TABLE `users` (
  `id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `discord_user_id` bigint unsigned NOT NULL,
  `global_name` varchar(255) DEFAULT NULL,
  `discriminator` varchar(10) DEFAULT NULL,
  `created_at` datetime NOT NULL,
  `updated_at` datetime NOT NULL,
  `character_name` varchar(255) DEFAULT NULL,
  `world` varchar(32) DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `ix_users_discord_user_id` (`discord_user_id`)
) ENGINE=InnoDB AUTO_INCREMENT=20 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `users`
--

LOCK TABLES `users` WRITE;
/*!40000 ALTER TABLE `users` DISABLE KEYS */;
INSERT INTO `users` VALUES
(1,87046372833910784,'thee oracle','0','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(2,331920838221758464,'Asaki Stormfall','0','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(3,475744554910351370,NULL,'5552','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(4,821995310179155999,'Blink','0','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(5,1307692839504838807,NULL,'9772','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(6,248324194692104194,'Snow Hey Oh','0','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(7,480510060796313630,'Sophia Pistis (The Eepiest)','0','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(8,788245484540002335,'RxDrStocks','0','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(9,235148962103951360,NULL,'1536','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(10,272824833346240512,'Yeyito','0','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(11,288156980009238529,'Oni Neko-Punch!','0','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(12,344822496022757379,'Danger','0','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(13,349251322610057216,NULL,'0','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(14,688159443317424149,'Marco A.','0','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(15,738246485141094530,'Jazrin','0','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(16,833812630615359498,'Liehn','0','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(17,1348488328629977153,NULL,'0102','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(18,1358112153152000022,'Bianka81','0','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL),
(19,1386358170934575285,NULL,'9311','2025-09-25 11:12:16','2025-09-25 11:12:16',NULL,NULL);
/*!40000 ALTER TABLE `users` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Dumping routines for database 'demibot'
--
/*!40103 SET TIME_ZONE=@OLD_TIME_ZONE */;

/*!40101 SET SQL_MODE=@OLD_SQL_MODE */;
/*!40014 SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS */;
/*!40014 SET UNIQUE_CHECKS=@OLD_UNIQUE_CHECKS */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;
/*M!100616 SET NOTE_VERBOSITY=@OLD_NOTE_VERBOSITY */;

-- Dump completed on 2025-09-25  6:34:52
