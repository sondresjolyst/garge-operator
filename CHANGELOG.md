# Changelog

## [1.4.4](https://github.com/sondresjolyst/garge-operator/compare/v1.4.3...v1.4.4) (2026-04-30)


### Bug Fixes

* add JsonPropertyName attribute to SensorStatePayload.Value ([#81](https://github.com/sondresjolyst/garge-operator/issues/81)) ([2100f4c](https://github.com/sondresjolyst/garge-operator/commit/2100f4cd323bb8777efae59cad8d936493e50a74))
* add JsonPropertyName attribute to SensorStatePayload.Value ([#81](https://github.com/sondresjolyst/garge-operator/issues/81)) ([b17319c](https://github.com/sondresjolyst/garge-operator/commit/b17319cc3767922aaf724fd72c0fbbaf64b8f1db))

## [1.4.3](https://github.com/sondresjolyst/garge-operator/compare/v1.4.2...v1.4.3) (2026-04-29)


### Bug Fixes

* re-apply development changes with conventional commits ([#79](https://github.com/sondresjolyst/garge-operator/issues/79)) ([5d33fba](https://github.com/sondresjolyst/garge-operator/commit/5d33fba1c102062c5406c2c2fe424fad9075df26))

## [1.4.2](https://github.com/sondresjolyst/garge-operator/compare/v1.4.1...v1.4.2) (2026-04-18)


### Bug Fixes

* automation socket target type ([#66](https://github.com/sondresjolyst/garge-operator/issues/66)) ([#67](https://github.com/sondresjolyst/garge-operator/issues/67)) ([fc20f40](https://github.com/sondresjolyst/garge-operator/commit/fc20f40148d7f9e128ec1627e87fe775ab8f7248))

## [1.4.1](https://github.com/sondresjolyst/garge-operator/compare/v1.4.0...v1.4.1) (2026-04-16)


### Bug Fixes

* automation socket target type ([#63](https://github.com/sondresjolyst/garge-operator/issues/63)) ([c8f5804](https://github.com/sondresjolyst/garge-operator/commit/c8f5804adc3e34c7490529fc54b28089eb93eef7))
* **worker:** accept any switch type as automation target ([#62](https://github.com/sondresjolyst/garge-operator/issues/62)) ([630d6c9](https://github.com/sondresjolyst/garge-operator/commit/630d6c9e0030a57a8ff27590f63c068fb5c912c8))

## [1.4.0](https://github.com/sondresjolyst/garge-operator/compare/v1.3.0...v1.4.0) (2026-04-14)


### Features

* **automations:** evaluate electricity price conditions in worker ([#57](https://github.com/sondresjolyst/garge-operator/issues/57)) ([a96649c](https://github.com/sondresjolyst/garge-operator/commit/a96649c52ef468aa9bb6c1dd0c41577cc3394a1d))
* **automations:** timed auto-off logic in worker ([#58](https://github.com/sondresjolyst/garge-operator/issues/58)) ([f8fb44d](https://github.com/sondresjolyst/garge-operator/commit/f8fb44d8929c8a8c7befe3e66cde6a525798ad9c))

## [1.3.0](https://github.com/sondresjolyst/garge-operator/compare/v1.2.4...v1.3.0) (2026-04-10)


### Features

* skip disabled automation rules and record last triggered timestamp ([#51](https://github.com/sondresjolyst/garge-operator/issues/51)) ([92296f8](https://github.com/sondresjolyst/garge-operator/commit/92296f854313142f309c824d9e15f4dbf02f6b04))

## [1.2.4](https://github.com/sondresjolyst/garge-operator/compare/v1.2.3...v1.2.4) (2026-04-09)


### Bug Fixes

* ignore retained state messages on reconnect ([#46](https://github.com/sondresjolyst/garge-operator/issues/46)) ([8695e25](https://github.com/sondresjolyst/garge-operator/commit/8695e25f75fc5afea1033df4f20d427c2af04de8))
* prevent duplicate switch creation on rapid config messages ([#47](https://github.com/sondresjolyst/garge-operator/issues/47)) ([6864f1c](https://github.com/sondresjolyst/garge-operator/commit/6864f1ca076cac1505d8a54adaf636737085a669))

## [1.2.3](https://github.com/sondresjolyst/garge-operator/compare/v1.2.2...v1.2.3) (2026-04-08)


### Bug Fixes

* skip battery sensor creation, derive voltage sensor name for routing ([905c1e5](https://github.com/sondresjolyst/garge-operator/commit/905c1e577f50fbe44f219437ea5957aeb544a47c))

## [1.2.2](https://github.com/sondresjolyst/garge-operator/compare/v1.2.1...v1.2.2) (2026-04-06)


### Bug Fixes

* update deserialisation for ReferenceHandler.IgnoreCycles ([#36](https://github.com/sondresjolyst/garge-operator/issues/36)) ([39be2ba](https://github.com/sondresjolyst/garge-operator/commit/39be2baae80844161c83831739c347291bc32dbe))

## [1.2.1](https://github.com/sondresjolyst/garge-operator/compare/v1.2.0...v1.2.1) (2026-03-27)


### Bug Fixes

* seed battery health sensor tracking on operator startup ([#28](https://github.com/sondresjolyst/garge-operator/issues/28)) ([b3e19e7](https://github.com/sondresjolyst/garge-operator/commit/b3e19e77b85903188a5b46833baffe203c8aa00d))

## [1.2.0](https://github.com/sondresjolyst/garge-operator/compare/v1.1.0...v1.2.0) (2026-03-26)


### Features

* forward battery health MQTT messages to API ([#25](https://github.com/sondresjolyst/garge-operator/issues/25)) ([3ddba58](https://github.com/sondresjolyst/garge-operator/commit/3ddba584815dd034d7e71ed8b08271700e10667f))

## [1.1.0](https://github.com/sondresjolyst/garge-operator/compare/v1.0.0...v1.1.0) (2025-08-10)


### Features

* automations ([#15](https://github.com/sondresjolyst/garge-operator/issues/15)) ([0dc3f03](https://github.com/sondresjolyst/garge-operator/commit/0dc3f03f545ee20a8372a50d9811e91c64ec2050))
* Discover Device and new mqtt structure ([#14](https://github.com/sondresjolyst/garge-operator/issues/14)) ([bd212b0](https://github.com/sondresjolyst/garge-operator/commit/bd212b0a519be453f61658c781d5da76388b3afe))

## 1.0.0 (2025-06-27)


### Features

* add switch support and webhook ([#3](https://github.com/sondresjolyst/garge-operator/issues/3)) ([318ad75](https://github.com/sondresjolyst/garge-operator/commit/318ad75d66a5d455c694b4cf60ece436bcf1e9ac))
* expose port to allow webhook connection ([#6](https://github.com/sondresjolyst/garge-operator/issues/6)) ([c691bdb](https://github.com/sondresjolyst/garge-operator/commit/c691bdbe06c32bb4374dca3ee985c9f20863bb1b))


### Bug Fixes

* uniqids only stored last value in list ([b8c4daa](https://github.com/sondresjolyst/garge-operator/commit/b8c4daa677342365336cf41e196194b8bd06d715))
