PROJECT_NAME := DMRoute-ng
PROJECT_CONFIG ?= Release
EDITOR := rider
DOC_OUTPUT_DIR ?= artifacts/docs
DOC_SOURCES := $(shell find docs -type f -name '*.adoc' ! -name 'handbook.adoc' ! -name 'hardware-tests.adoc')

include dotnet.mk

.PHONY: docs docs-check docs-html docs-pdf

docs: docs-html docs-pdf

docs-check:
	@set -e; for file in $(DOC_SOURCES); do \
		bundle exec asciidoctor --failure-level WARN --out-file /dev/null "$$file"; \
	done

docs-html: docs-check
	mkdir -p $(DOC_OUTPUT_DIR)
	bundle exec asciidoctor --failure-level WARN -D $(DOC_OUTPUT_DIR) -o DMRoute-ng-Handbuch.html docs/handbook.adoc
	bundle exec asciidoctor --failure-level WARN -D $(DOC_OUTPUT_DIR) -o DMRoute-ng-Hardwaretests.html docs/hardware-tests.adoc

docs-pdf: docs-check
	mkdir -p $(DOC_OUTPUT_DIR)
	bundle exec asciidoctor-pdf --failure-level WARN -D $(DOC_OUTPUT_DIR) -o DMRoute-ng-Handbuch.pdf docs/handbook.adoc
	bundle exec asciidoctor-pdf --failure-level WARN -D $(DOC_OUTPUT_DIR) -o DMRoute-ng-Hardwaretests.pdf docs/hardware-tests.adoc
