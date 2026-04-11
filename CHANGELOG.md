# Changelog

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
