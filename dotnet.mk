.PHONY: help run test add rm list restore clean distclean edit docker docker.tar

MAKEFLAGS += --silent

PROJECT_NAME ?=$(shell basename `pwd`) # TODO: Fix
PROJECT_CONFIG ?= Debug
NET_VERSION ?= net8.0
BINARY = bin/$(PROJECT_CONFIG)/$(NET_VERSION)/$(PROJECT_NAME).dll
CS_FILES = $(shell find . -type f -name '*.cs')

EDITOR ?= rider

DOCKER_IMAGE_NAME ?= $(shell echo $(PROJECT_NAME) | tr '[:upper:]' '[:lower:]' )
DOCKER_IMAGE_TAG ?= latest

### Targets ###

$(BINARY): $(CS_FILES)
	dotnet build -c $(PROJECT_CONFIG)

help:
	@echo "Target         | Description"
	@echo "---------------+------------------------------------------"
	@echo "help           | Shows this help"
	@echo "run            | Runs the project"
	@echo "test           | Runs the tests"
	@echo "add PKG=<name> | Adds package"
	@echo "rm PKG=<name>  | Removes package"
	@echo "list           | List packages"
	@echo "restore        | Restores project"
	@echo "clean          | Removes build artefacts"
	@echo "distclean      | Removes all build artefacts"
	@echo "edit           | Open project in rider"
	@echo "docker         | Build docker image"
	@echo "docker.tar     | Build docker image and export as tar file"
	@echo ""
	@echo "Default Action is to compile the project"

run: $(BINARY)
	dotnet run -c $(PROJECT_CONFIG)

test: $(BINARY)
	dotnet test

add:
	test $(PKG)
	dotnet add package $(PKG)

rm:
	test $(PKG)
	dotnet remove package $(PKG)

list:
	dotnet list package

restore:
	dotnet restore

ifdef USE_KAITAI
force-gen: $(GEN_FILES)
	mkdir -p Packets
	kaitai-struct-compiler $(GEN_FILES) --outdir $(GEN_FOLDER)/ --target csharp --dotnet-namespace $(GEN_NAMESPACE)
endif

clean:
	dotnet clean
	rm -f ${DOCKER_IMAGE_NAME}-${DOCKER_IMAGE_TAG}.tar

distclean: clean
	rm -rf bin/
	rm -rf obj/

edit:
	$(EDITOR) `pwd`

docker: Dockerfile
	docker buildx build -t ${DOCKER_IMAGE_NAME}:${DOCKER_IMAGE_TAG} -f Dockerfile .

docker.tar: docker
	docker image save -o ${DOCKER_IMAGE_NAME}-${DOCKER_IMAGE_TAG}.tar ${DOCKER_IMAGE_NAME}:${DOCKER_IMAGE_TAG}
